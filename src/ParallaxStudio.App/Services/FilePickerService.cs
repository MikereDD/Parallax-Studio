using System.IO;
using Microsoft.Win32;

namespace ParallaxStudio.App.Services;

public sealed class FilePickerService
{
    public string? PickMediaFile(string? initialDirectory)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open media",
            CheckFileExists = true,
            Multiselect = false,
            Filter = "Video files|*.mkv;*.mp4;*.m4v;*.avi;*.mov;*.wmv;*.webm;*.ts;*.m2ts;*.mpg;*.mpeg|All files|*.*"
        };

        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
    public string? PickSubtitleFile(string? initialDirectory)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Load external subtitle",
            CheckFileExists = true,
            Multiselect = false,
            Filter = "Subtitle files|*.srt;*.ass;*.ssa;*.vtt;*.sub;*.idx;*.sup|All files|*.*"
        };

        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
