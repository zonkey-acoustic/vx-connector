namespace VxProxy;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        bool directMode = args.Any(a =>
            a.Equals("--direct", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("-d", StringComparison.OrdinalIgnoreCase))
            || IsInfiniteTeesOnPort921();

        Application.Run(new TrayApplicationContext(directMode));
    }

    private static bool IsInfiniteTeesOnPort921()
    {
        var iniPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "InfiniteTees", "Saved", "Config", "Windows", "GameUserSettings.ini");

        if (!File.Exists(iniPath))
            return false;

        var content = File.ReadAllText(iniPath);
        return content.Contains("Port=921");
    }
}
