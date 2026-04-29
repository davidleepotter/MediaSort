using System;
using System.IO;
using System.Windows;
using MediaSort.Models;
using MediaSort.Services;

namespace MediaSort.Views;

public partial class ConflictDialog : Window
{
    public ConflictPolicy Choice { get; private set; } = ConflictPolicy.Skip;
    public bool ApplyToAll => ApplyAllCheck.IsChecked == true;

    /// <summary>
    /// New constructor with side-by-side thumbnail preview.
    /// </summary>
    public ConflictDialog(string sourcePath, string existingPath)
    {
        InitializeComponent();

        var fileName = Path.GetFileName(sourcePath);
        var destFolder = Path.GetDirectoryName(existingPath) ?? string.Empty;
        MessageText.Text = $"\"{fileName}\" already exists in:\n{destFolder}\n\nRename keeps both files. Overwrite replaces the existing file. Skip leaves the source file unchanged.";

        PopulateSide(sourcePath, SourceNameText, SourceMetaText, SourceImage, SourcePlaceholder);
        PopulateSide(existingPath, ExistingNameText, ExistingMetaText, ExistingImage, ExistingPlaceholder);

        SourceInitialized += (_, _) => WindowChrome.ApplyCurrentTheme(this);
    }

    private static void PopulateSide(string path,
        System.Windows.Controls.TextBlock nameText,
        System.Windows.Controls.TextBlock metaText,
        System.Windows.Controls.Image image,
        System.Windows.Controls.TextBlock placeholder)
    {
        try
        {
            nameText.Text = Path.GetFileName(path);

            if (!File.Exists(path))
            {
                metaText.Text = "(file not found)";
                placeholder.Visibility = Visibility.Visible;
                return;
            }

            var fi = new FileInfo(path);
            var sizeStr = FormatSize(fi.Length);
            var modStr = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm");
            var ext = Path.GetExtension(path).ToLowerInvariant();

            string dimsStr = string.Empty;
            if (MediaFormats.ImageExtensions.Contains(ext))
            {
                var (w, h) = ThumbnailLoader.TryReadImageDimensions(path);
                if (w > 0 && h > 0) dimsStr = $"{w} × {h}";
            }
            else if (MediaFormats.VideoExtensions.Contains(ext))
            {
                dimsStr = "video";
            }

            metaText.Text = string.IsNullOrEmpty(dimsStr)
                ? $"{sizeStr}  •  {modStr}"
                : $"{dimsStr}  •  {sizeStr}  •  {modStr}";

            // Try to load a preview. Images decode directly; videos fall back to placeholder.
            if (MediaFormats.ImageExtensions.Contains(ext))
            {
                var bmp = ThumbnailLoader.LoadFull(path);
                if (bmp != null)
                {
                    image.Source = bmp;
                }
                else
                {
                    placeholder.Visibility = Visibility.Visible;
                }
            }
            else
            {
                placeholder.Text = MediaFormats.VideoExtensions.Contains(ext) ? "▶" : "—";
                placeholder.Visibility = Visibility.Visible;
            }
        }
        catch
        {
            placeholder.Visibility = Visibility.Visible;
        }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        double kb = bytes / 1024.0;
        if (kb < 1024) return $"{kb:0.#} KB";
        double mb = kb / 1024.0;
        if (mb < 1024) return $"{mb:0.##} MB";
        double gb = mb / 1024.0;
        return $"{gb:0.##} GB";
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        Choice = ConflictPolicy.Rename;
        DialogResult = true;
    }

    private void Overwrite_Click(object sender, RoutedEventArgs e)
    {
        Choice = ConflictPolicy.Overwrite;
        DialogResult = true;
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        Choice = ConflictPolicy.Skip;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
