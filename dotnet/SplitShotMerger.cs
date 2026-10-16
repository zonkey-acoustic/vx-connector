using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VxProxy;

/// <summary>
/// Rejoins a swing that ProTee Labs split across two messages, before it reaches the sim.
///
/// PTL sends a swing either as one combined message (ContainsBallData and
/// ContainsClubData both true), or as ball-only followed ~100–270ms later by
/// club-only under the same ShotNumber. Every sim we've tested handles the combined
/// shape, and every one mishandles the split shape differently (2026-10-16, with
/// testing/ptl-sim.py):
///   - Infinite Tees records one shot but plays the swing sound twice,
///   - The Fitting Bay records two shots, one of them blank,
///   - ShotForge 1.2.7 keeps the ball-only message and drops the club data.
/// So the split shape is turned into the combined one here, and the sims only ever see
/// the shape they all get right.
///
/// A ball-only message is held for up to <see cref="DefaultHold"/>. If the club-only
/// partner arrives in time, one merged message goes out: the ball-only message with the
/// club-only message's ClubData and ContainsClubData=true. Anything else, or no partner
/// in time, releases the held message unchanged, so the worst case is today's
/// behaviour plus the hold.
///
/// Message order is preserved: anything arriving while a message is held releases the
/// held one first. Forwarding happens under the lock for the same reason — the timer
/// and the listener's read loop must not interleave their sends.
/// </summary>
internal sealed class SplitShotMerger : IDisposable
{
    /// <summary>
    /// Covers the observed 100–270ms gap with room to spare. Only split swings pay it;
    /// combined messages and status chatter pass straight through.
    /// </summary>
    public static readonly TimeSpan DefaultHold = TimeSpan.FromMilliseconds(600);

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        // Keep the rewrite as close to PTL's bytes as we can; the default encoder
        // escapes characters PTL sends literally.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private enum Shape { Other, BallOnly, ClubOnly }

    private readonly Action<string> _forward;
    private readonly Action<string> _log;
    private readonly TimeSpan _hold;
    private readonly object _gate = new();
    private readonly System.Threading.Timer _timer;

    private string? _held;
    private int _heldShot;
    private long _heldSinceTicks;   // Environment.TickCount64 when _held was taken
    private bool _disposed;

    public SplitShotMerger(Action<string> forward, Action<string> log, TimeSpan? hold = null)
    {
        _forward = forward;
        _log = log;
        _hold = hold ?? DefaultHold;
        _timer = new System.Threading.Timer(OnHoldExpired);
    }

    /// <summary>One message from ProTee Labs, in arrival order.</summary>
    public void Offer(string json)
    {
        var shape = Classify(json, out int shot);

        lock (_gate)
        {
            if (_disposed) return;

            if (_held is not null)
            {
                if (shape == Shape.ClubOnly && shot == _heldShot)
                {
                    var held = TakeHeld();
                    var merged = TryMerge(held, json);
                    if (merged is not null)
                    {
                        _log($"  Merged split ball/club messages for #{shot} into one shot.");
                        _forward(merged);
                    }
                    else
                    {
                        _forward(held);
                        _forward(json);
                    }
                    return;
                }

                _forward(TakeHeld());
            }

            if (shape == Shape.BallOnly)
            {
                _held = json;
                _heldShot = shot;
                _heldSinceTicks = Environment.TickCount64;
                _timer.Change(_hold, Timeout.InfiniteTimeSpan);
                return;
            }

            _forward(json);
        }
    }

    /// <summary>
    /// Drop a held message because the ProTee Labs connection it came from has ended.
    /// Forwarding it is no use: the sim link closes along with PTL's. Keeping it would be
    /// worse, since it would go out ahead of the next session's first message.
    /// </summary>
    public void Discard()
    {
        lock (_gate)
        {
            if (_disposed || _held is null) return;
            _log($"  Dropped ball-only #{_heldShot}: ProTee Labs disconnected before its club data arrived.");
            TakeHeld();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _held = null;
            _timer.Dispose();
        }
    }

    private void OnHoldExpired(object? _)
    {
        lock (_gate)
        {
            if (_disposed || _held is null) return;

            // A callback already queued when its message was released can land on the
            // next held message. If that one hasn't served its full hold, re-arm instead.
            var remaining = _hold - TimeSpan.FromMilliseconds(Environment.TickCount64 - _heldSinceTicks);
            if (remaining > TimeSpan.Zero)
            {
                _timer.Change(remaining, Timeout.InfiniteTimeSpan);
                return;
            }

            _log($"  No club data followed #{_heldShot} within {_hold.TotalMilliseconds:F0}ms — sent it ball-only.");
            _forward(TakeHeld());
        }
    }

    // Caller holds _gate.
    private string TakeHeld()
    {
        var held = _held!;
        _held = null;
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        return held;
    }

    private static Shape Classify(string json, out int shotNumber)
    {
        shotNumber = 0;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Shape.Other;

            // Without a ShotNumber there's nothing to pair on.
            if (!root.TryGetProperty("ShotNumber", out var sn)
                || sn.ValueKind != JsonValueKind.Number
                || !sn.TryGetInt32(out shotNumber))
                return Shape.Other;

            if (!root.TryGetProperty("ShotDataOptions", out var o) || o.ValueKind != JsonValueKind.Object)
                return Shape.Other;
            bool ball = o.TryGetProperty("ContainsBallData", out var b) && b.ValueKind == JsonValueKind.True;
            bool club = o.TryGetProperty("ContainsClubData", out var c) && c.ValueKind == JsonValueKind.True;

            return (ball, club) switch
            {
                (true, false) => Shape.BallOnly,
                (false, true) => Shape.ClubOnly,
                _ => Shape.Other,
            };
        }
        catch (JsonException)
        {
            return Shape.Other;
        }
    }

    /// <summary>
    /// The ball-only message, with the club-only message's ClubData and the club flag set.
    /// Built from the ball message so its BallData is the one PTL sent as "the" ball data;
    /// numbers keep PTL's exact text because JsonNode writes parsed values back raw.
    /// Null if either message won't parse into the expected shape.
    /// </summary>
    private static string? TryMerge(string ballJson, string clubJson)
    {
        try
        {
            if (JsonNode.Parse(ballJson) is not JsonObject ball) return null;
            if (JsonNode.Parse(clubJson)?["ClubData"] is not JsonObject clubData) return null;
            if (ball["ShotDataOptions"] is not JsonObject options) return null;

            ball["ClubData"] = clubData.DeepClone();
            options["ContainsClubData"] = true;
            return ball.ToJsonString(WriteOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
