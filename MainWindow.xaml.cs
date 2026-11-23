using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;

namespace CustomSensitivity;

public partial class MainWindow : Window
{
    private const uint SensitivityId = 0x02F2;
    private const int MinimumValue = -100;
    private const int MaximumValue = 200;
    private bool _updating;
    private CancellationTokenSource? _successFeedbackCancellation;

    public MainWindow()
    {
        InitializeComponent();
        LoadProfiles();
    }

    private void LoadProfiles()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var rockstar = Path.Combine(documents, "Rockstar Games");
        var profiles = new List<ProfileEntry>();
        foreach (var (edition, folder) in new[] { ("Enhanced", "GTAV Enhanced"), ("Legacy", "GTA V") })
        {
            var profileRoot = Path.Combine(rockstar, folder, "Profiles");
            if (!Directory.Exists(profileRoot)) continue;
            foreach (var path in Directory.EnumerateFiles(profileRoot, "pc_settings.bin", SearchOption.AllDirectories))
            {
                try
                {
                    var bytes = File.ReadAllBytes(path);
                    var offset = FindSettingValueOffset(bytes);
                    if (offset >= 0)
                        profiles.Add(new ProfileEntry(edition, Path.GetFileName(Path.GetDirectoryName(path)!), path, BitConverter.ToInt32(bytes, offset), File.GetLastWriteTime(path)));
                }
                catch { /* a profile may be being saved; omit it until Refresh */ }
            }
        }
        ProfileSelector.ItemsSource = profiles.OrderByDescending(p => p.LastWriteTime).ToList();
        ProfileSelector.SelectedIndex = ProfileSelector.Items.Count > 0 ? 0 : -1;
        StatusText.Text = profiles.Count == 0 ? "No valid GTA V profile was found." : "Close GTA V before applying a change. A timestamped backup is created automatically.";
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => LoadProfiles();

    private void BrowseProfileFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select GTA V pc_settings.bin",
            Filter = "GTA V settings (pc_settings.bin)|pc_settings.bin|All files (*.*)|*.*",
            FileName = "pc_settings.bin",
            InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Rockstar Games")
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var bytes = File.ReadAllBytes(dialog.FileName);
            var offset = FindSettingValueOffset(bytes);
            if (offset < 0) throw new InvalidDataException("This file does not contain the sensitivity record 0x02F2.");
            var custom = new ProfileEntry("Custom", Path.GetFileName(Path.GetDirectoryName(dialog.FileName)!), dialog.FileName, BitConverter.ToInt32(bytes, offset), File.GetLastWriteTime(dialog.FileName));
            var entries = (ProfileSelector.ItemsSource as IEnumerable<ProfileEntry> ?? []).Where(p => !p.Path.Equals(custom.Path, StringComparison.OrdinalIgnoreCase)).ToList();
            entries.Insert(0, custom);
            ProfileSelector.ItemsSource = entries;
            ProfileSelector.SelectedItem = custom;
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 150));
            StatusText.Text = "Close GTA V before applying a change. A timestamped backup is created automatically.";
        }
        catch (Exception ex)
        {
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(235, 120, 100));
            StatusText.Text = "Could not use this file: " + ex.Message;
        }
    }

    private void ShowProfileFile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileSelector.SelectedItem is not ProfileEntry profile) { StatusText.Text = "Select a GTA V profile first."; return; }
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{profile.Path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { StatusText.Text = "Could not open file location: " + ex.Message; }
    }

    private void ProfileSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProfileSelector.SelectedItem is not ProfileEntry profile) return;
        try
        {
            // A profile switch always discards un-applied editor input and
            // reloads the actual value from the newly selected file.
            var bytes = File.ReadAllBytes(profile.Path);
            var offset = FindSettingValueOffset(bytes);
            if (offset < 0) throw new InvalidDataException("Record 0x02F2 was not found.");
            SetValue(BitConverter.ToInt32(bytes, offset));
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 150));
            StatusText.Text = "Close GTA V before applying a change. A timestamped backup is created automatically.";
        }
        catch (Exception ex) { StatusText.Text = "Could not read profile: " + ex.Message; }
    }

    private void SensitivitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating) return;
        _updating = true;
        SensitivityBox.Text = Math.Round(e.NewValue).ToString(CultureInfo.InvariantCulture);
        _updating = false;
    }

    private void SensitivityBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating || !int.TryParse(SensitivityBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) return;
        // The text field deliberately permits values beyond the slider range.
        // In that case the slider remains parked at its nearest edge, while the
        // exact typed number stays intact and is the number written to the file.
        if (value < MinimumValue || value > MaximumValue) return;
        _updating = true; SensitivitySlider.Value = value; _updating = false;
    }

    private void SensitivityBox_LostFocus(object sender, RoutedEventArgs e) => NormalizeTextValue();
    private void SensitivityBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { NormalizeTextValue(); Keyboard.ClearFocus(); } }

    private void SensitivityBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !IsValidSensitivityText(((TextBox)sender).Text, ((TextBox)sender).SelectionStart,
            ((TextBox)sender).SelectionLength, e.Text);
    }

    private void SensitivityBox_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (!e.DataObject.GetDataPresent(DataFormats.Text)) { e.CancelCommand(); return; }
        var box = (TextBox)sender;
        var pasted = e.DataObject.GetData(DataFormats.Text) as string ?? string.Empty;
        if (!IsValidSensitivityText(box.Text, box.SelectionStart, box.SelectionLength, pasted))
            e.CancelCommand();
    }

    private static bool IsValidSensitivityText(string current, int selectionStart, int selectionLength, string insertion)
    {
        var candidate = current.Remove(selectionStart, selectionLength).Insert(selectionStart, insertion);
        // Empty text and a sole leading minus are valid intermediate states
        // while editing. The saved value must still fit a signed Int32.
        return candidate is "" or "-" || int.TryParse(candidate, NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out _);
    }

    private int NormalizeTextValue()
    {
        if (!int.TryParse(SensitivityBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) value = (int)Math.Round(SensitivitySlider.Value);
        _updating = true;
        SensitivityBox.Text = value.ToString(CultureInfo.InvariantCulture);
        SensitivitySlider.Value = Math.Clamp(value, MinimumValue, MaximumValue);
        _updating = false;
        return value;
    }

    private void SetValue(int value)
    {
        _updating = true;
        SensitivitySlider.Value = Math.Clamp(value, MinimumValue, MaximumValue);
        SensitivityBox.Text = value.ToString(CultureInfo.InvariantCulture);
        _updating = false;
    }

    private void SensitivitySlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var slider = (Slider)sender;
        SetSliderValueFromPointer(slider, e.GetPosition(slider).X);
        // The entire track is a drag surface, not just the round thumb.
        // Capturing the mouse also keeps the drag stable if the pointer leaves
        // the thin visual line.
        Mouse.Capture(slider);
        e.Handled = true;
    }

    private void SensitivitySlider_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        var slider = (Slider)sender;
        if (!slider.IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed) return;
        SetSliderValueFromPointer(slider, e.GetPosition(slider).X);
    }

    private void SensitivitySlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var slider = (Slider)sender;
        if (slider.IsMouseCaptured) slider.ReleaseMouseCapture();
    }

    private static void SetSliderValueFromPointer(Slider slider, double pointerX)
    {
        const double thumbDiameter = 14;
        var usableWidth = Math.Max(1, slider.ActualWidth - thumbDiameter);
        var ratio = Math.Clamp((pointerX - (thumbDiameter / 2)) / usableWidth, 0d, 1d);
        slider.Value = slider.Minimum + ((slider.Maximum - slider.Minimum) * ratio);
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        _successFeedbackCancellation?.Cancel();
        ApplyButton.ClearValue(Control.TagProperty);
        if (Process.GetProcessesByName("GTA5").Any()) { StatusText.Text = "GTA5.exe is running. Close the game completely before changing its profile."; return; }
        if (ProfileSelector.SelectedItem is not ProfileEntry profile) { StatusText.Text = "Select a GTA V profile first."; return; }
        var value = NormalizeTextValue();

        ApplyButton.IsEnabled = false;
        ApplyButton.Content = "Applying…";
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
        try
        {
            var bytes = File.ReadAllBytes(profile.Path);
            var offset = FindSettingValueOffset(bytes);
            if (offset < 0) throw new InvalidDataException("Record 0x02F2 was not found.");
            var oldValue = BitConverter.ToInt32(bytes, offset);
            if (oldValue == value) { StatusText.Text = "The selected profile already has this value."; return; }
            var backup = profile.Path + ".sensitivity-backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".bin";
            File.Copy(profile.Path, backup, overwrite: false);
            BitConverter.GetBytes(value).CopyTo(bytes, offset);
            var temporary = profile.Path + ".tmp";
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, profile.Path, overwrite: true);
            RefreshProfileEntry(profile, value);
            await ShowTemporarySuccessAsync($"Applied {value}. Backup: {Path.GetFileName(backup)}");
        }
        catch (Exception ex)
        {
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(235, 120, 100));
            StatusText.Text = "Could not apply change: " + ex.Message;
        }
        finally
        {
            if (ApplyButton.Content?.ToString() == "Applying…")
                ResetApplyButton();
        }
    }

    private async Task ShowTemporarySuccessAsync(string message)
    {
        _successFeedbackCancellation?.Cancel();
        _successFeedbackCancellation?.Dispose();
        _successFeedbackCancellation = new CancellationTokenSource();
        var token = _successFeedbackCancellation.Token;

        StatusText.Foreground = new SolidColorBrush(Color.FromRgb(126, 205, 112));
        StatusText.Text = message;
        ApplyButton.IsEnabled = true;
        ApplyButton.Content = "Applied ✓";
        ApplyButton.Tag = "success";
        ApplyButton.Background = new SolidColorBrush(Color.FromRgb(75, 151, 78));
        ApplyButton.Foreground = new SolidColorBrush(Color.FromRgb(245, 245, 245));

        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1200), token);
            ResetApplyButton();

            await Task.Delay(TimeSpan.FromMilliseconds(13800), token);
            if (StatusText.Text == message)
            {
                StatusText.Text = string.Empty;
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 150));
            }
        }
        catch (TaskCanceledException) { }
    }

    private void ResetApplyButton()
    {
        ApplyButton.Content = "Apply sensitivity";
        ApplyButton.ClearValue(Control.TagProperty);
        ApplyButton.ClearValue(Control.BackgroundProperty);
        ApplyButton.ClearValue(Control.ForegroundProperty);
        ApplyButton.IsEnabled = true;
    }

    private void RefreshProfileEntry(ProfileEntry profile, int value)
    {
        var updated = profile with { Value = value, LastWriteTime = File.GetLastWriteTime(profile.Path) };
        var entries = (ProfileSelector.ItemsSource as IEnumerable<ProfileEntry> ?? [])
            .Select(entry => entry.Path.Equals(profile.Path, StringComparison.OrdinalIgnoreCase) ? updated : entry)
            .ToList();
        ProfileSelector.ItemsSource = entries;
        ProfileSelector.SelectedItem = updated;
    }

    private static int FindSettingValueOffset(byte[] bytes)
    {
        var matches = new List<int>();
        var id = BitConverter.GetBytes(SensitivityId);
        for (var index = 0; index <= bytes.Length - 8; index++)
            if (bytes[index] == id[0] && bytes[index + 1] == id[1] && bytes[index + 2] == id[2] && bytes[index + 3] == id[3]) matches.Add(index + 4);
        return matches.Count == 1 ? matches[0] : -1;
    }

    private sealed record ProfileEntry(string Edition, string ProfileId, string Path, int Value, DateTime LastWriteTime)
    {
        public string DisplayName => $"{Edition}  •  {ProfileId}  •  current: {Value}  •  changed: {LastWriteTime:dd.MM.yyyy HH:mm}";
        public override string ToString()
        {
            var userFolder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\');
            return Path.StartsWith(userFolder, StringComparison.OrdinalIgnoreCase)
                ? Path[userFolder.Length..].TrimStart('\\')
                : Path;
        }
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        var mica = 2;
        DwmSetWindowAttribute(handle, 38, ref mica, sizeof(int));
        var dark = 1;
        DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
