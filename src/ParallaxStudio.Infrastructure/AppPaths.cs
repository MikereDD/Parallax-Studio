using ParallaxStudio.Core;

namespace ParallaxStudio.Infrastructure;

public sealed class AppPaths
{
    public AppPaths()
    {
        Root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppIdentity.StorageFolderName);
        Logs = Path.Combine(Root, "logs");
        Cache = Path.Combine(Root, "cache");
        SettingsFile = Path.Combine(Root, "settings.json");
        ResumeFile = Path.Combine(Root, "resume.json");
        LogFile = Path.Combine(Logs, "parallax-studio.log");
    }

    public string Root { get; }
    public string Logs { get; }
    public string Cache { get; }
    public string SettingsFile { get; }
    public string ResumeFile { get; }
    public string LogFile { get; }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Cache);
    }
}
