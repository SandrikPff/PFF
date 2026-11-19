using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Media;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace CrimsonPanel;

public partial class MainWindow : Window
{
    // Windows Raw Input observes keyboard input without swallowing the key from other applications.
    // No hooks, injection or game process access. Some protected/fullscreen games may restrict delivery.
    private const int WmInput = 0x00FF;
    private const uint RidInput = 0x10000003;
    private const uint RidevInputSink = 0x00000100;
    private const ushort RimTypeKeyboard = 1;
    private const ushort RiKeyBreak = 1;
    [StructLayout(LayoutKind.Sequential)] private struct RawInputDevice
    {
        public ushort UsagePage, Usage;
        public uint Flags;
        public IntPtr Target;
    }
    [StructLayout(LayoutKind.Sequential)] private struct RawInputHeader
    {
        public uint Type, Size;
        public IntPtr Device;
        public IntPtr WParam;
    }
    [StructLayout(LayoutKind.Sequential)] private struct RawKeyboard
    {
        public ushort MakeCode, Flags, Reserved, VKey;
        public uint Message, ExtraInformation;
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterRawInputDevices([In] RawInputDevice[] devices, uint count, uint size);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetRawInputData(IntPtr rawInput, uint command, IntPtr data, ref uint size, uint headerSize);
    private readonly System.Collections.Generic.HashSet<int> _keysDown = new();
    private bool _rawInputReady;
    private string _currentTab = "headphones";
    private bool _wheelDragging;
    private readonly Stopwatch _stopwatch = new();
    private int _stopwatchKey = 0x76; // F7
    private bool _capturingStopwatch;
    private bool _audioLoading;
    private bool _audioEnabled = true;
    private bool _synchronizingAudio;
    private bool _audioSyncQueued;
    private bool _closing;
    private int _audioConnectionRevision;
    private (string Key, string Status)? _outputConnection;
    private MicrophonePipe? _microphonePipe;
    private string? _enabledMicrophoneGuid;
    private string? _managedCaptureGuid;
    private bool _captureMigrationPending = true;
    private bool _testCaptureRequested;
    private string? _restoreCaptureRequested;
    private bool _closeAfterAudio;
    private string? _savedOutputGuid;
    private string? _savedInputGuid;
    private string _outputPreset = "Стандарт";
    private string _inputPreset = "Стандарт";
    private readonly DispatcherTimer _audioSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly int[] _freq = ApoProfiles.Frequencies;
    private readonly double[] _outputEq = new double[20];
    private readonly double[] _inputEq = new double[20];
    private readonly string _audioSettingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pff_crimson", "audio-profiles.json");


    private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly DispatcherTimer _metroTimer = new();
    private readonly Stopwatch _countWatch = new();
    private readonly Stopwatch _metroWatch = new();
    private double _seconds = 5;
    private int _bpm = 120;
    private int _hotkey = 0x75; // F6 virtual-key code
    private int _sound = 0;
    private bool _capturing;
    private SoundPlayer[] _sounds = Array.Empty<SoundPlayer>();
    private bool _running;
    private bool _metroRunning;
    private long _metroBeat;
    private bool _lightTheme;
    private string _accent = "#D12A2A";
    private string _customSound = "";
    private SoundPlayer? _customPlayer;
    private bool _initializing = true;
    private readonly string _settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pff_crimson", "settings.json");

    public MainWindow()
    {
        InitializeComponent();
        LoadSettings();
        LoadSounds();
        SecondsBox.Text = _seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        BpmBox.Text = _bpm.ToString();
        UpdateHotkeyButton();
        SoundCombo.ItemsSource = new[] { "Мягкий", "Цифровой", "Звонок", "Метроном", "Мой звук" };
        if (_sound == 4 && !File.Exists(_customSound)) _sound = 0;
        SoundCombo.SelectedIndex = _sound;
        CustomSoundName.Text = string.IsNullOrEmpty(_customSound) ? "Файл не выбран" : Path.GetFileName(_customSound);
        ThemeSwitch.IsChecked = _lightTheme;
        DrawColorWheel();
        ApplyTheme();
        _initializing = false;
        _audioLoading = true;
        BuildEqSliders(OutputBands, _outputEq);
        BuildEqSliders(InputBands, _inputEq);
        LoadAudioProfile();
        PopulateAudioDevices();
        _audioLoading = false;
        _audioSaveTimer.Tick += (_, _) =>
        {
            _audioSaveTimer.Stop();
            SaveAudioProfile();
            SynchronizeAudio();
        };
        UpdateAudioReadouts();
        RefreshPresetLists();
        Loaded += (_, _) => SynchronizeAudio();
        StopwatchHotkeyButton.Content = KeyName(_stopwatchKey);
        UpdateBpm();
        UpdateCountdown();
        ShowTab("headphones");
        _uiTimer.Tick += UiTick;
        _uiTimer.Start();
        _metroTimer.Interval = TimeSpan.FromMilliseconds(10);
        _metroTimer.Tick += MetroTick;
        SourceInitialized += (_, _) =>
        {
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
            RegisterKeyboardObserver();
        };

    }

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(_settingsPath)) return;
            var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(_settingsPath));
            if (settings is null) return;
            _seconds = Math.Clamp(settings.Seconds, 0.1, 86400);
            _bpm = Math.Clamp(settings.Bpm, 20, 300);
            _hotkey = settings.Hotkey is >= 1 and <= 12 ? 0x70 + settings.Hotkey - 1 : settings.Hotkey;
            if (_hotkey < 0x08 || _hotkey > 0xFE) _hotkey = 0x75;
            _sound = Math.Clamp(settings.Sound, 0, 4);
            _lightTheme = settings.LightTheme;
            _accent = string.IsNullOrWhiteSpace(settings.Accent) ? "#D12A2A" : settings.Accent;
            _customSound = settings.CustomSound ?? "";
            if (settings.Width >= MinWidth) Width = settings.Width;
            if (settings.Height >= MinHeight) Height = settings.Height;
        }
        catch { /* Invalid settings: use defaults. */ }
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(new Settings(_seconds, _bpm, _hotkey, Width, Height, _sound, _lightTheme, _accent, _customSound)));
        }
        catch { /* Non-critical; app remains usable. */ }
    }

    private sealed record Settings(double Seconds, int Bpm, int Hotkey, double Width, double Height, int Sound = 0, bool LightTheme = false, string Accent = "#D12A2A", string CustomSound = "");

    private void RegisterKeyboardObserver()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var devices = new[] { new RawInputDevice { UsagePage = 1, Usage = 6, Flags = RidevInputSink, Target = hwnd } };
        _rawInputReady = RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<RawInputDevice>());
        HotkeyButton.ToolTip = _rawInputReady ? "Клавиша отслеживается без блокировки ввода" : "Windows не разрешила фоновое отслеживание клавиатуры";
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmInput || !_rawInputReady) return IntPtr.Zero;
        uint size = 0;
        uint headerSize = (uint)Marshal.SizeOf<RawInputHeader>();
        if (GetRawInputData(lParam, RidInput, IntPtr.Zero, ref size, headerSize) == uint.MaxValue || size == 0 || size > 65536) return IntPtr.Zero;
        var ptr = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(lParam, RidInput, ptr, ref size, headerSize) == uint.MaxValue) return IntPtr.Zero;
            var header = Marshal.PtrToStructure<RawInputHeader>(ptr);
            if (header.Type != RimTypeKeyboard) return IntPtr.Zero;
            var kb = Marshal.PtrToStructure<RawKeyboard>(IntPtr.Add(ptr, Marshal.SizeOf<RawInputHeader>()));
            int vk = kb.VKey;
            if ((kb.Flags & RiKeyBreak) != 0) _keysDown.Remove(vk);
            else if (_keysDown.Add(vk) && !_capturing && !_capturingStopwatch)
            {
                bool typing = IsActive && Keyboard.FocusedElement is TextBox;
                bool modifiers = _keysDown.Overlaps(new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5 });
                if (!typing && !modifiers)
                {
                    if (vk == _stopwatchKey && _currentTab == "stopwatch" && MenuPanel.Visibility != Visibility.Visible) ToggleStopwatch();
                    else if (vk == _hotkey && _currentTab == "timer" && MenuPanel.Visibility != Visibility.Visible) StartTimer();
                }
            }
        }
        finally { Marshal.FreeHGlobal(ptr); }
        return IntPtr.Zero; // never mark keyboard input handled
    }

    private void UiTick(object? sender, EventArgs e)
    {
        ClockText.Text = DateTime.Now.ToString("HH:mm:ss");
        LargeClockText.Text = DateTime.Now.ToString("HH:mm:ss");
        ClockDateText.Text = DateTime.Now.ToString("dddd, d MMMM yyyy", new System.Globalization.CultureInfo("ru-RU"));
        var sw = _stopwatch.Elapsed;
        StopwatchText.Text = $"{(int)sw.TotalMinutes:00}:{sw.Seconds:00}.{sw.Milliseconds / 10:00}";
        if (_running && _countWatch.Elapsed.TotalSeconds >= _seconds)
        {
            _countWatch.Reset();
            _running = false;
            TimerButton.Content = "СТАРТ";
            PlaySound();
        }
        UpdateCountdown();
    }

    private void UpdateCountdown()
    {
        var remaining = _running ? Math.Max(0, _seconds - _countWatch.Elapsed.TotalSeconds) : _seconds;
        var ts = TimeSpan.FromSeconds(remaining);
        CountdownText.Text = $"{(int)ts.TotalMinutes:00}:{ts.Seconds:00}.{ts.Milliseconds / 100}";
    }

    private void StartTimer()
    {
        if (_currentTab != "timer" || MenuPanel.Visibility == Visibility.Visible) return;
        CommitSeconds();
        _countWatch.Restart();
        _running = true;
        TimerButton.Content = "ПЕРЕЗАПУСК";
        UpdateCountdown();
    }

    private void StopTimer()
    {
        _countWatch.Reset();
        _running = false;
        TimerButton.Content = "СТАРТ";
        UpdateCountdown();
    }

    private void CommitSeconds()
    {
        if (double.TryParse(SecondsBox.Text.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) && double.IsFinite(value))
            _seconds = Math.Clamp(value, 0.1, 86400);
        SecondsBox.Text = _seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        SaveSettings();
        if (!_running) UpdateCountdown();
    }

    private void CommitBpm()
    {
        if (int.TryParse(BpmBox.Text, out var value)) _bpm = Math.Clamp(value, 20, 300);
        BpmBox.Text = _bpm.ToString();
        UpdateBpm();
        if (_metroRunning) { _metroBeat = 0; _metroWatch.Restart(); PlayMetronomeClick(); _metroBeat++; }
        SaveSettings();
    }

    private void UpdateBpm() => BpmText.Text = $"{_bpm} BPM";
    private void MetroTick(object? sender, EventArgs e)
    {
        var targetBeat = (long)Math.Floor(_metroWatch.Elapsed.TotalMinutes * _bpm);
        if (targetBeat >= _metroBeat)
        {
            PlayMetronomeClick();
            _metroBeat = targetBeat + 1;
        }
    }

    private void ToggleMetronome()
    {
        if (_currentTab != "metronome" && !_metroRunning) return;
        if (_metroRunning)
        {
            _metroTimer.Stop(); _metroWatch.Reset(); _metroRunning = false;
            MetronomeButton.Content = "ЗАПУСТИТЬ";
        }
        else
        {
            CommitBpm();
            _metroBeat = 1;
            _metroWatch.Restart();
            PlayMetronomeClick();
            _metroRunning = true;
            _metroTimer.Start();
            MetronomeButton.Content = "ОСТАНОВИТЬ";
        }
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is Button || FindParentButton(e.OriginalSource as DependencyObject)) return;
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
    private static bool FindParentButton(DependencyObject? element)
    {
        while (element != null)
        {
            if (element is Button) return true;
            element = System.Windows.Media.VisualTreeHelper.GetParent(element);
        }
        return false;
    }
    private void ResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        Width = Math.Max(MinWidth, Width + e.HorizontalChange);
        Height = Math.Max(MinHeight, Height + e.VerticalChange);
    }
    private void ShowTab(string tab)
    {
        if (_currentTab != tab)
        {
            if (_currentTab == "timer") StopTimer();
            if (_currentTab == "stopwatch" && _stopwatch.IsRunning)
            { _stopwatch.Stop(); StopwatchButton.Content = "ПРОДОЛЖИТЬ"; }
            if (_currentTab == "metronome" && _metroRunning) ToggleMetronome();
        }
        _currentTab = tab;
        TimerPanel.Visibility = tab == "timer" ? Visibility.Visible : Visibility.Collapsed;
        MetronomePanel.Visibility = tab == "metronome" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = tab == "settings" ? Visibility.Visible : Visibility.Collapsed;
        HeadphonesPanel.Visibility = tab == "headphones" ? Visibility.Visible : Visibility.Collapsed;
        MicrophonePanel.Visibility = tab == "microphone" ? Visibility.Visible : Visibility.Collapsed;
        StopwatchPanel.Visibility = tab == "stopwatch" ? Visibility.Visible : Visibility.Collapsed;
        ClockPanel.Visibility = tab == "clock" ? Visibility.Visible : Visibility.Collapsed;
    }
    private void Menu_Click(object sender, RoutedEventArgs e)
    {
        bool opening = MenuPanel.Visibility != Visibility.Visible;
        MenuPanel.Visibility = opening ? Visibility.Visible : Visibility.Collapsed;
        if (opening)
        {
            if (_currentTab == "timer") StopTimer();
            if (_currentTab == "stopwatch" && _stopwatch.IsRunning) { _stopwatch.Stop(); StopwatchButton.Content = "ПРОДОЛЖИТЬ"; }
            if (_currentTab == "metronome" && _metroRunning) ToggleMetronome();
            TimerPanel.Visibility = Visibility.Collapsed;
            MetronomePanel.Visibility = Visibility.Collapsed;
            SettingsPanel.Visibility = Visibility.Collapsed;
            HeadphonesPanel.Visibility = Visibility.Collapsed;
            MicrophonePanel.Visibility = Visibility.Collapsed;
            StopwatchPanel.Visibility = Visibility.Collapsed;
            ClockPanel.Visibility = Visibility.Collapsed;
        }
        else ShowTab(_currentTab);
    }
    private void HeadphonesTab_Click(object sender, RoutedEventArgs e) { MenuPanel.Visibility = Visibility.Collapsed; ShowTab("headphones"); }
    private void MicrophoneTab_Click(object sender, RoutedEventArgs e) { MenuPanel.Visibility = Visibility.Collapsed; ShowTab("microphone"); }
    private void StopwatchTab_Click(object sender, RoutedEventArgs e) { MenuPanel.Visibility = Visibility.Collapsed; ShowTab("stopwatch"); }
    private void ClockTab_Click(object sender, RoutedEventArgs e) { MenuPanel.Visibility = Visibility.Collapsed; ShowTab("clock"); }
    private void ToggleStopwatch()
    {
        if (_currentTab != "stopwatch" || MenuPanel.Visibility == Visibility.Visible) return;
        if (_stopwatch.IsRunning) { _stopwatch.Stop(); StopwatchButton.Content = "ПРОДОЛЖИТЬ"; }
        else { _stopwatch.Start(); StopwatchButton.Content = "ПАУЗА"; }
    }
    private void StopwatchButton_Click(object sender, RoutedEventArgs e) => ToggleStopwatch();
    private void StopwatchReset_Click(object sender, RoutedEventArgs e) { _stopwatch.Reset(); StopwatchButton.Content = "СТАРТ"; StopwatchText.Text = "00:00.00"; }
    private void StopwatchHotkey_Click(object sender, RoutedEventArgs e) { _capturingStopwatch = true; StopwatchHotkeyButton.Content = "ESC — отмена"; StopwatchHotkeyButton.Focus(); }
    private void StopwatchHotkey_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturingStopwatch) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape) { _capturingStopwatch = false; StopwatchHotkeyButton.Content = KeyName(_stopwatchKey); return; }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        var vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk < 0x08 || vk > 0xFE || vk == _hotkey || key is Key.Tab or Key.Enter or Key.Space) return;
        _stopwatchKey = vk;
        _capturingStopwatch = false;
        _keysDown.Clear();
        StopwatchHotkeyButton.Content = KeyName(vk);
        SaveAudioProfile();
    }
    private void TimerTab_Click(object sender, RoutedEventArgs e) { MenuPanel.Visibility = Visibility.Collapsed; ShowTab("timer"); }
    private void MetronomeTab_Click(object sender, RoutedEventArgs e) { MenuPanel.Visibility = Visibility.Collapsed; ShowTab("metronome"); }
    private void SettingsTab_Click(object sender, RoutedEventArgs e) { MenuPanel.Visibility = Visibility.Collapsed; ShowTab("settings"); }
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void TimerButton_Click(object sender, RoutedEventArgs e) => StartTimer();
    private void ResetButton_Click(object sender, RoutedEventArgs e) => StopTimer();
    private void SecondsBox_LostFocus(object sender, RoutedEventArgs e) => CommitSeconds();
    private static string KeyName(int vk) => KeyInterop.KeyFromVirtualKey(vk).ToString();
    private void UpdateHotkeyButton() => HotkeyButton.Content = KeyName(_hotkey);
    private void HotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        _capturing = true;
        HotkeyButton.Content = "ESC — отмена";
        HotkeyButton.Focus();
    }
    private void HotkeyButton_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturing) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape) { _capturing = false; UpdateHotkeyButton(); return; }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        if (Keyboard.Modifiers != ModifierKeys.None)
        {
            HotkeyButton.ToolTip = "Используйте клавишу без Ctrl/Alt/Shift/Win";
            return;
        }
        var vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk < 0x08 || vk > 0xFE || key is Key.Tab or Key.Enter or Key.Space) { HotkeyButton.ToolTip = "Эта клавиша не поддерживается"; return; }
        _capturing = false;
        _hotkey = vk;
        UpdateHotkeyButton();
        _keysDown.Clear();
        SaveSettings();
    }
    private void LoadSounds()
    {
        // A missing/corrupt audio resource must never prevent the main window from opening.
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            _sounds = new[] { "soft", "digital", "bell", "metronome" }.Select(name =>
            {
                var stream = assembly.GetManifestResourceStream($"CrimsonPanel.Assets.{name}.wav");
                if (stream is null) throw new InvalidOperationException($"Не найден звук: {name}");
                var player = new SoundPlayer(stream);
                player.Load();
                return player;
            }).ToArray();
        }
        catch (Exception ex)
        {
            App.LogError("Загрузка звуков", ex);
            _sounds = Array.Empty<SoundPlayer>();
        }
    }
    private void PlaySound() { try { if (_sound == 4) { EnsureCustomSound(); _customPlayer?.Play(); } else if (_sound >= 0 && _sound < _sounds.Length) _sounds[_sound].Play(); } catch (Exception ex) { App.LogError("Воспроизведение звука", ex); } }
    private void PlayMetronomeClick() => PlaySound();
    private void EnsureCustomSound() { if (_customPlayer == null && File.Exists(_customSound)) { _customPlayer = new SoundPlayer(_customSound); _customPlayer.Load(); } }
    private void SoundCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || SoundCombo.SelectedIndex < 0) return;
        if (SoundCombo.SelectedIndex == 4 && !File.Exists(_customSound)) { SoundCombo.SelectedIndex = _sound; return; }
        _sound = SoundCombo.SelectedIndex; PlaySound(); SaveSettings();
    }
    private void ImportSound_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Выберите звук WAV", Filter = "Звуковые файлы WAV (*.wav)|*.wav" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pff_crimson");
            Directory.CreateDirectory(directory);
            using (var validation = new SoundPlayer(dialog.FileName))
                validation.Load();
            var destination = Path.Combine(directory, "custom.wav");
            if (!string.Equals(Path.GetFullPath(dialog.FileName), destination, StringComparison.OrdinalIgnoreCase))
                File.Copy(dialog.FileName, destination, true);
            _customPlayer?.Dispose();
            _customPlayer = new SoundPlayer(destination);
            _customPlayer.Load();
            _customSound = destination;
            CustomSoundName.Text = Path.GetFileName(dialog.FileName);
            bool alreadySelected = SoundCombo.SelectedIndex == 4;
            SoundCombo.SelectedIndex = 4;
            if (alreadySelected)
            {
                PlaySound();
                SaveSettings();
            }
        }
        catch (Exception ex) { MessageBox.Show("Не удалось загрузить WAV: " + ex.Message, "PFF"); }
    }
    private void ThemeSwitch_Click(object sender, RoutedEventArgs e) { _lightTheme = ThemeSwitch.IsChecked == true; ApplyTheme(); SaveSettings(); }
    private static Color ColorFrom(string hex, Color fallback)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); } catch { return fallback; }
    }
    private static Color Shift(Color c, double factor) => Color.FromRgb((byte)Math.Clamp(c.R * factor, 0, 255), (byte)Math.Clamp(c.G * factor, 0, 255), (byte)Math.Clamp(c.B * factor, 0, 255));
    private void SetBrush(string name, Color color)
    {
        // XAML resources may be frozen. Replace the resource instead of mutating it.
        Resources[name] = new SolidColorBrush(color);
    }
    private void ApplyTheme()
    {
        var accent = ColorFrom(_accent, Colors.Crimson);
        SetBrush("AccentBrush", accent);
        SetBrush("ActionBrush", Shift(accent, .82));
        SetBrush("TitleBrush", accent);
        SetBrush("MainBrush", _lightTheme ? Color.FromRgb(245,245,247) : Color.FromRgb(17,17,17));
        SetBrush("HeaderBrush", _lightTheme ? Color.FromRgb(225,225,229) : Color.FromRgb(32,32,32));
        SetBrush("InputBrush", _lightTheme ? Color.FromRgb(226,226,230) : Color.FromRgb(37,37,37));
        SetBrush("TextBrush", _lightTheme ? Colors.Black : Colors.White);
        SetBrush("MutedBrush", _lightTheme ? Color.FromRgb(85,85,85) : Color.FromRgb(187,187,187));
        ThemeLabel.Text = _lightTheme ? "Светлая" : "Тёмная";
        SoundCombo.Background = (Brush)Resources["InputBrush"];
        SoundCombo.Foreground = (Brush)Resources["TextBrush"];
    }
    private static Color Hsv(double hue, double sat, double value)
    {
        var h = hue / 60.0; var c = value * sat; var x = c * (1 - Math.Abs(h % 2 - 1));
        var (r,g,b) = h switch { < 1 => (c,x,0.0), < 2 => (x,c,0.0), < 3 => (0.0,c,x), < 4 => (0.0,x,c), < 5 => (x,0.0,c), _ => (c,0.0,x) };
        var m = value-c; return Color.FromRgb((byte)((r+m)*255), (byte)((g+m)*255), (byte)((b+m)*255));
    }
    private void DrawColorWheel()
    {
        const int size=240; var pixels = new byte[size*size*4];
        for (int y=0;y<size;y++) for (int x=0;x<size;x++)
        {
            double dx=x-(size-1)/2.0, dy=y-(size-1)/2.0, radius=Math.Sqrt(dx*dx+dy*dy)/((size-1)/2.0);
            int i=(y*size+x)*4;
            if (radius>1) continue;
            double hue=(Math.Atan2(dy,dx)*180/Math.PI+360)%360;
            var c=Hsv(hue,Math.Min(1,radius),1);
            pixels[i]=c.B;pixels[i+1]=c.G;pixels[i+2]=c.R;pixels[i+3]=255;
        }
        ColorWheel.Source=BitmapSource.Create(size,size,96,96,PixelFormats.Bgra32,null,pixels,size*4);
    }
    private void UpdateColorAt(Point pos)
    {
        var dx = pos.X - ColorWheel.ActualWidth / 2;
        var dy = pos.Y - ColorWheel.ActualHeight / 2;
        var radius = Math.Sqrt(dx * dx + dy * dy) / (Math.Min(ColorWheel.ActualWidth, ColorWheel.ActualHeight) / 2);
        if (radius > 1) return;
        var color = Hsv((Math.Atan2(dy, dx) * 180 / Math.PI + 360) % 360, Math.Min(1, radius), 1);
        _accent = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        ApplyTheme();
    }
    private void ColorWheel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _wheelDragging = true;
        ColorWheel.CaptureMouse();
        UpdateColorAt(e.GetPosition(ColorWheel));
        e.Handled = true;
    }
    private void ColorWheel_MouseMove(object sender, MouseEventArgs e)
    {
        if (_wheelDragging && e.LeftButton == MouseButtonState.Pressed) UpdateColorAt(e.GetPosition(ColorWheel));
    }
    private void ColorWheel_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_wheelDragging) return;
        UpdateColorAt(e.GetPosition(ColorWheel));
        _wheelDragging = false;
        ColorWheel.ReleaseMouseCapture();
        SaveSettings();
    }
    private void ColorWheel_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (!_wheelDragging) return;
        _wheelDragging = false;
        SaveSettings();
    }
    private void ResetAccent_Click(object sender, RoutedEventArgs e) { _accent="#D12A2A";ApplyTheme();SaveSettings(); }
    private void Numeric_PreviewTextInput(object sender, TextCompositionEventArgs e) => e.Handled = !System.Text.RegularExpressions.Regex.IsMatch(e.Text, "^[0-9.,]+$");
    private void BpmBox_LostFocus(object sender, RoutedEventArgs e) => CommitBpm();
    private void BpmMinus_Click(object sender, RoutedEventArgs e) { CommitBpm(); BpmBox.Text = Math.Max(20, _bpm - 1).ToString(); CommitBpm(); }
    private void BpmPlus_Click(object sender, RoutedEventArgs e) { CommitBpm(); BpmBox.Text = Math.Min(300, _bpm + 1).ToString(); CommitBpm(); }
    private void MetronomeButton_Click(object sender, RoutedEventArgs e) => ToggleMetronome();

    private void PopulateAudioDevices()
    {
        PopulateAudioDevices(OutputDevices, _savedOutputGuid, input: false);
        PopulateAudioDevices(InputDevices, _savedInputGuid, input: true);
    }

    private void PopulateAudioDevices(ComboBox combo, string? preferred, bool input)
    {
        combo.Items.Clear();
        try
        {
            foreach (var endpoint in AudioEndpoints.Enumerate(input).Where(endpoint => !MicrophonePipe.IsCable(endpoint, input)))
                combo.Items.Add(endpoint);
        }
        catch (Exception ex)
        {
            App.LogError(input ? "Список микрофонов Windows" : "Список устройств воспроизведения Windows", ex);
        }
        if (preferred is null)
            combo.SelectedIndex = combo.Items.Count > 0 ? 0 : -1;
        else
            combo.SelectedItem = combo.Items.OfType<AudioEndpoints.Endpoint>()
                .FirstOrDefault(endpoint => string.Equals(endpoint.Guid, preferred, StringComparison.OrdinalIgnoreCase));
    }

    private void BuildEqSliders(StackPanel panel, double[] values)
    {
        for (int i = 0; i < _freq.Length; i++)
        {
            int index = i;
            var column = new StackPanel { Width = 47, Margin = new Thickness(1, 0, 1, 0) };
            var readout = new TextBlock { Text = "0", Foreground = (Brush)Resources["AccentBrush"], HorizontalAlignment = HorizontalAlignment.Center };
            readout.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "AccentBrush");
            var slider = new Slider { Orientation = Orientation.Vertical, Height = 155, Width = 38, Minimum = -12, Maximum = 12, Value = values[i], TickFrequency = 1, IsSnapToTickEnabled = true };
            slider.ValueChanged += (_, _) => { values[index] = slider.Value; readout.Text = slider.Value.ToString("+0;-0;0"); if (!_audioLoading) QueueAudioSave(); };
            var freq = new TextBlock { Text = _freq[i] >= 1000 ? $"{_freq[i] / 1000.0:0.#}k" : _freq[i].ToString(), Foreground = (Brush)Resources["MutedBrush"], HorizontalAlignment = HorizontalAlignment.Center, FontSize = 11 };
            column.Children.Add(readout); column.Children.Add(slider); column.Children.Add(freq);
            panel.Children.Add(column);
        }
    }
    private const int BandLayout = 2;

    private sealed class AudioProfile
    {
        public int FrequencyLayout { get; set; }
        public string? VirtualMicrophoneGuid { get; set; }
        public string? ManagedCaptureGuid { get; set; }
        public bool PhysicalCaptureMigrationComplete { get; set; }
        public int StopwatchKey { get; set; } = 0x76;
        public string? OutputDeviceGuid { get; set; }
        public string? InputDeviceGuid { get; set; }
        public bool? ProcessingEnabled { get; set; }
        public string? OutputPreset { get; set; }
        public string? InputPreset { get; set; }
        public double[] OutputEq { get; set; } = new double[20];
        public double[] InputEq { get; set; } = new double[20];
        public double OutputGain { get; set; }
        public double InputGain { get; set; }
        public double OutputCompression { get; set; }
        public double InputCompression { get; set; }
        public double OutputReverb { get; set; }
        public double InputReverb { get; set; }
    }
    private void LoadAudioProfile()
    {
        try
        {
            if (!File.Exists(_audioSettingsPath)) return;
            var p = JsonSerializer.Deserialize<AudioProfile>(File.ReadAllText(_audioSettingsPath));
            if (p == null) return;
            _stopwatchKey = p.StopwatchKey is >= 8 and <= 254 && p.StopwatchKey != _hotkey ? p.StopwatchKey : 0x76;
            _savedOutputGuid = Guid.TryParseExact(p.OutputDeviceGuid, "B", out _) ? p.OutputDeviceGuid : null;
            _savedInputGuid = Guid.TryParseExact(p.InputDeviceGuid, "B", out _) ? p.InputDeviceGuid : null;
            _audioEnabled = p.ProcessingEnabled ?? true;
            _enabledMicrophoneGuid = ReadEndpointGuid(p.VirtualMicrophoneGuid);
            _managedCaptureGuid = ReadEndpointGuid(p.ManagedCaptureGuid);
            _captureMigrationPending = !p.PhysicalCaptureMigrationComplete;
            var outputBands = p.FrequencyLayout == BandLayout ? p.OutputEq : ApoProfiles.MigrateBands(p.OutputEq);
            var inputBands = p.FrequencyLayout == BandLayout ? p.InputEq : ApoProfiles.MigrateBands(p.InputEq);
            _outputPreset = p.OutputPreset ?? "Стандарт";
            _inputPreset = p.InputPreset ?? "Стандарт";
            for (int i = 0; i < 20; i++)
            {
                if (outputBands is not null && i < outputBands.Length) _outputEq[i] = Math.Clamp(outputBands[i], -12, 12);
                if (inputBands is not null && i < inputBands.Length) _inputEq[i] = Math.Clamp(inputBands[i], -12, 12);
                ((Slider)((StackPanel)OutputBands.Children[i]).Children[1]).Value = _outputEq[i];
                ((Slider)((StackPanel)InputBands.Children[i]).Children[1]).Value = _inputEq[i];
            }
            OutputGain.Value = p.OutputGain; InputGain.Value = p.InputGain;
            OutputCompression.Value = p.OutputCompression; InputCompression.Value = p.InputCompression;
            OutputReverb.Value = p.FrequencyLayout == BandLayout ? p.OutputReverb : 0; InputReverb.Value = p.InputReverb;
        }
        catch (Exception ex) { App.LogError("Загрузка аудиопрофиля", ex); }
    }
    private void SaveAudioProfile()
    {
        if (_audioLoading) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_audioSettingsPath)!);
            var p = new AudioProfile
            {
                FrequencyLayout = BandLayout,
                VirtualMicrophoneGuid = _enabledMicrophoneGuid,
                ManagedCaptureGuid = _managedCaptureGuid,
                PhysicalCaptureMigrationComplete = !_captureMigrationPending,
                StopwatchKey = _stopwatchKey,
                ProcessingEnabled = _audioEnabled,
                OutputPreset = _outputPreset,
                InputPreset = _inputPreset,
                OutputDeviceGuid = _savedOutputGuid,
                InputDeviceGuid = _savedInputGuid,
                OutputEq = _outputEq, InputEq = _inputEq,
                OutputGain = OutputGain.Value, InputGain = InputGain.Value,
                OutputCompression = OutputCompression.Value, InputCompression = InputCompression.Value,
                OutputReverb = OutputReverb.Value, InputReverb = InputReverb.Value
            };
            File.WriteAllText(_audioSettingsPath, JsonSerializer.Serialize(p));
        }
        catch (Exception ex) { App.LogError("Сохранение аудиопрофиля", ex); }

    }
    private void AudioSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_audioLoading || _initializing) return;
        UpdateAudioReadouts();
        QueueAudioSave();
    }

    private void QueueAudioSave()
    {
        _audioSaveTimer.Stop();
        _audioSaveTimer.Start();
    }

    private void UpdateAudioReadouts()
    {
        if (OutputGainValue == null) return;
        OutputGainValue.Text = $"{OutputGain.Value:+0;-0;0} дБ";
        InputGainValue.Text = $"{InputGain.Value:+0;-0;0} дБ";
        OutputCompressionValue.Text = $"{OutputCompression.Value:0}%";
        InputCompressionValue.Text = $"{InputCompression.Value:0}%";
        OutputReverbValue.Text = $"{OutputReverb.Value:0}%";
        InputReverbValue.Text = $"{InputReverb.Value:0}%";
    }
    private void DeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_audioLoading || _initializing || sender is not ComboBox selector ||
            selector.SelectedItem is not AudioEndpoints.Endpoint endpoint)
            return;
        if (ReferenceEquals(selector, OutputDevices)) _savedOutputGuid = endpoint.Guid;
        else
        {
            if (_savedInputGuid != endpoint.Guid) _enabledMicrophoneGuid = null;
            _savedInputGuid = endpoint.Guid;
        }
        SaveAudioProfile();
        SynchronizeAudio();
    }

    private void AudioDevices_DropDownOpened(object sender, EventArgs e)
    {
        if (sender is not ComboBox selector) return;
        bool input = ReferenceEquals(selector, InputDevices);
        string? before = (selector.SelectedItem as AudioEndpoints.Endpoint)?.Guid;
        _audioLoading = true;
        try { PopulateAudioDevices(selector, input ? _savedInputGuid : _savedOutputGuid, input); }
        finally { _audioLoading = false; }
        if (before != (selector.SelectedItem as AudioEndpoints.Endpoint)?.Guid)
            SynchronizeAudio();
    }

    private sealed class SavedPreset
    {
        public int FrequencyLayout { get; set; }
        public double[] Bands { get; set; } = new double[20];
        public double Gain { get; set; }
        public double Compression { get; set; }
        public double Effect { get; set; }
    }
    private static string PresetFolder(bool output) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pff_crimson", "presets", output ? "headphones" : "microphone");
    private void RefreshPresetLists()
    {
        foreach (var output in new[] { true, false })
        {
            var combo = output ? OutputDevicesPresets : InputDevicesPresets;
            combo.Items.Clear();
            var folder = PresetFolder(output);
            if (Directory.Exists(folder))
                foreach (var file in Directory.GetFiles(folder, "*.json").OrderBy(f => f)) combo.Items.Add(Path.GetFileNameWithoutExtension(file));
            combo.Items.Insert(0, "Стандарт");
            var name = output ? _outputPreset : _inputPreset;
            combo.SelectedItem = combo.Items.Contains(name) ? name : "Стандарт";
        }
    }
    private void SavePreset_Click(object sender, RoutedEventArgs e)
    {
        bool output = (sender as Button)?.Tag as string == "OutputDevices";
        string name = (output ? OutputDevicesPresetName : InputDevicesPresetName).Text.Trim();
        if (string.Equals(name, "Стандарт", StringComparison.OrdinalIgnoreCase)) { MessageBox.Show("Стандарт — встроенный пресет, выберите другое название."); return; }
        if (name.Length == 0 || name.Length > 60 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        { MessageBox.Show("Название: 1–60 символов, без запрещённых символов имени файла."); return; }
        try
        {
            string folder = PresetFolder(output); Directory.CreateDirectory(folder);
            var preset = new SavedPreset { FrequencyLayout = BandLayout, Bands = (double[])(output ? _outputEq : _inputEq).Clone(), Gain = output ? OutputGain.Value : InputGain.Value,
                Compression = output ? OutputCompression.Value : InputCompression.Value, Effect = output ? OutputReverb.Value : InputReverb.Value };
            File.WriteAllText(Path.Combine(folder, name + ".json"), JsonSerializer.Serialize(preset));
            RefreshPresetLists();
            SetSelectedPreset(output, name);
            SaveAudioProfile();
        }
        catch (Exception ex) { App.LogError("Сохранение пресета", ex); MessageBox.Show(ex.Message); }
    }
    private void ApplyPreset_Click(object sender, RoutedEventArgs e)
    {
        bool output = (sender as Button)?.Tag as string == "OutputDevices";
        string? name = (output ? OutputDevicesPresets : InputDevicesPresets).SelectedItem as string;
        if (string.IsNullOrEmpty(name)) return;
        if (name == "Стандарт")
        {
            SetSelectedPreset(output, name);
            ApplyEqValues(output, new double[20], 0, 0, 0);
            return;
        }
        try
        {
            var preset = JsonSerializer.Deserialize<SavedPreset>(File.ReadAllText(Path.Combine(PresetFolder(output), name + ".json")));
            if (preset == null) return;
            SetSelectedPreset(output, name);
            bool legacy = preset.FrequencyLayout != BandLayout;
            ApplyEqValues(output, legacy ? ApoProfiles.MigrateBands(preset.Bands) : preset.Bands,
                preset.Gain, preset.Compression, legacy && output ? 0 : preset.Effect);
        }
        catch (Exception ex) { App.LogError("Загрузка пресета", ex); MessageBox.Show(ex.Message); }
    }
    private void ResetEq_Click(object sender, RoutedEventArgs e)
    {
        bool output = (sender as Button)?.Tag as string == "OutputDevices";
        SetSelectedPreset(output, "Стандарт");
        ApplyEqValues(output, new double[20], 0, 0, 0);
    }
    private void SetSelectedPreset(bool output, string name)
    {
        if (output) _outputPreset = name;
        else _inputPreset = name;
        (output ? OutputDevicesPresets : InputDevicesPresets).SelectedItem = name;
    }

    private void ApplyEqValues(bool output, double[] bands, double gain, double compression, double effect)
    {
        _audioLoading = true;
        try
        {
            var values = output ? _outputEq : _inputEq;
            var panel = output ? OutputBands : InputBands;
            for (int i = 0; i < 20; i++)
            {
                values[i] = bands is not null && i < bands.Length ? Math.Clamp(bands[i], -12, 12) : 0;
                ((Slider)((StackPanel)panel.Children[i]).Children[1]).Value = values[i];
            }
            (output ? OutputGain : InputGain).Value = Math.Clamp(gain, -12, 24);
            (output ? OutputCompression : InputCompression).Value = Math.Clamp(compression, 0, 100);
            (output ? OutputReverb : InputReverb).Value = Math.Clamp(effect, 0, 100);
        }
        finally { _audioLoading = false; }
        UpdateAudioReadouts();
        SaveAudioProfile();
        SynchronizeAudio();
    }
    private static string? ReadEndpointGuid(string? value) =>
        Guid.TryParseExact(value, "B", out var guid) ? guid.ToString("B") : null;

    private async void InstallMicrophoneDriver_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        button.IsEnabled = false;
        try
        {
            if (AudioEndpoints.Enumerate(input: false).Any(endpoint => MicrophonePipe.IsCable(endpoint, input: false)) &&
                AudioEndpoints.Enumerate(input: true).Any(endpoint => MicrophonePipe.IsCable(endpoint, input: true)))
            {
                ApoInputStatusText.Text = "VB-CABLE уже работает. Нажмите «Включить микрофон PFF».";
                return;
            }
            string script = Path.Combine(AppContext.BaseDirectory, "install-mic-driver.ps1");
            if (!File.Exists(script))
                throw new FileNotFoundException("Запустите установщик PFF для установки компонента.");
            string powershell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
            var start = new ProcessStartInfo(powershell)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            };
            foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script })
                start.ArgumentList.Add(arg);
            ApoInputStatusText.Text = "Загружаю VB-CABLE. В мастере нажмите Install Driver.";
            using var process = Process.Start(start) ?? throw new InvalidOperationException("PowerShell не запустился.");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var diagnostics = await Task.WhenAll(error, output);
            if (process.ExitCode is not (0 or 3010))
            {
                string message = diagnostics[0].Trim();
                throw new InvalidOperationException(message.Length == 0
                    ? $"Установка VB-CABLE завершилась с кодом {process.ExitCode}. Повторите запуск через PowerShell."
                    : message);
            }
            ApoInputStatusText.Text = process.ExitCode == 3010
                ? "VB-CABLE установлен. Перезагрузите Windows и включите микрофон PFF."
                : "VB-CABLE уже установлен. Нажмите «Включить микрофон PFF».";
        }
        catch (Exception ex)
        {
            ApoInputStatusText.Text = ex.Message;
            App.LogError("Установка VB-CABLE", ex);
        }
        finally { button.IsEnabled = true; }
    }

    private void EnableMicrophone_Click(object sender, RoutedEventArgs e)
    {
        if (InputDevices.SelectedItem is not AudioEndpoints.Endpoint) return;
        _testCaptureRequested = true;
        SynchronizeAudio();
    }

    private void DisconnectCapture_Click(object sender, RoutedEventArgs e)
    {
        _testCaptureRequested = false;
        _enabledMicrophoneGuid = null;
        _restoreCaptureRequested = (InputDevices.SelectedItem as AudioEndpoints.Endpoint)?.Guid;
        StopMicrophonePipe();
        SaveAudioProfile();
        SynchronizeAudio();
    }

    private void ConnectApo_Click(object sender, RoutedEventArgs e)
    {
        _audioEnabled = true;
        _audioConnectionRevision++;
        _outputConnection = null;
        SaveAudioProfile();
        SynchronizeAudio();
    }

    private async void SynchronizeAudio()
    {
        _audioSyncQueued = true;
        if (_synchronizingAudio || _closing) return;
        _synchronizingAudio = true;
        do
        {
            _audioSyncQueued = false;
            try { await ApplyAudioAsync(); }
            catch (Exception ex)
            {
                ApoStatusText.Text = ApoInputStatusText.Text = "Ошибка обработки звука. " + ex.Message;
                App.LogError("Синхронизация звука", ex);
            }
        } while (_audioSyncQueued && !_closing);
        _synchronizingAudio = false;
        if (_closeAfterAudio) Close();
    }

    private async Task ApplyAudioAsync()
    {
        var installation = ApoBridge.Find();
        bool hasCompressor = installation?.Compressor is not null;
        OutputCompression.IsEnabled = hasCompressor;
        OutputCompression.ToolTip = hasCompressor
            ? "Динамическая компрессия ReaComp" : "Нужен ReaComp из установщика PFF.";
        if (installation is null || !installation.SupportsAutomaticRouting)
        {
            ApoStatusText.Text = "Запустите установщик PFF для установки звукового движка.";
            var microphone = InputDevices.SelectedItem as AudioEndpoints.Endpoint;
            if (_restoreCaptureRequested is { } requested)
            {
                ApoBridge.DisableCaptureEnhancements(requested);
                _restoreCaptureRequested = null;
            }
            bool start = _testCaptureRequested;
            _testCaptureRequested = false;
            UpdateMicrophonePipe(microphone, start);
            return;
        }
        var output = OutputDevices.SelectedItem as AudioEndpoints.Endpoint;
        var input = InputDevices.SelectedItem as AudioEndpoints.Endpoint;
        CaptureTestButton.IsEnabled = false;
        CaptureOffButton.IsEnabled = false;
        InputDevices.IsEnabled = false;
        try
        {
            if (_captureMigrationPending)
            {
                _managedCaptureGuid ??= _restoreCaptureRequested ?? ApoBridge.ReadCaptureRoute(installation);
                _captureMigrationPending = false;
                SaveAudioProfile();
            }
            bool startCapture = _testCaptureRequested && input is not null;
            _testCaptureRequested = false;
            try { ApplyAudioProfiles(installation, _audioEnabled ? output : null); }
            finally
            {
                // Restoring capture must work even if a config edit failed.
                if (_managedCaptureGuid is { } oldCapture)
                {
                    await RestoreCaptureAsync(installation, oldCapture);
                    if (_restoreCaptureRequested == oldCapture) _restoreCaptureRequested = null;
                }
                if (_restoreCaptureRequested is { } requested)
                {
                    await RestoreCaptureAsync(installation, requested);
                    _restoreCaptureRequested = null;
                }
            }

            if (_audioEnabled)
            {
                int revision = _audioConnectionRevision;
                var opened = await ConnectOutputEndpointAsync(installation, output, _outputConnection);
                if (revision == _audioConnectionRevision) _outputConnection = opened;
            }
            else
                ApoStatusText.Text = "Обработка наушников отключена.";

            UpdateMicrophonePipe(input, startCapture);
        }
        finally
        {
            CaptureTestButton.IsEnabled = true;
            CaptureOffButton.IsEnabled = true;
            InputDevices.IsEnabled = true;
        }
    }

    private void UpdateMicrophonePipe(AudioEndpoints.Endpoint? input, bool start)
    {
        if (input is null)
        {
            StopMicrophonePipe();
            ApoInputStatusText.Text = "Выбранный микрофон недоступен.";
            return;
        }

        if (!start && _enabledMicrophoneGuid != input.Guid)
        {
            StopMicrophonePipe();
            ApoInputStatusText.Text = "Обычная запись Windows. Нажмите «Включить микрофон PFF» для обработки через VB-CABLE.";
            return;
        }

        var settings = new ApoProfiles.Settings(input.Guid, (double[])_inputEq.Clone(),
            InputGain.Value, InputCompression.Value, InputReverb.Value);
        if (_microphonePipe is { } active && active.SourceGuid == input.Guid)
        {
            active.Update(settings);
            return;
        }

        StopMicrophonePipe();
        try
        {
            var cable = AudioEndpoints.Enumerate(input: false).FirstOrDefault(endpoint => MicrophonePipe.IsCable(endpoint, input: false));
            var cableOutput = AudioEndpoints.Enumerate(input: true).FirstOrDefault(endpoint => MicrophonePipe.IsCable(endpoint, input: true));
            if (cable is null || cableOutput is null)
                throw new InvalidOperationException("Не найден VB-CABLE. Установите компонент и перезапустите Windows.");
            var pipe = new MicrophonePipe(input, cable, settings);
            pipe.Failed += error => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!ReferenceEquals(_microphonePipe, pipe)) return;
                _enabledMicrophoneGuid = null;
                StopMicrophonePipe();
                SaveAudioProfile();
                ApoInputStatusText.Text = "Связь с виртуальным микрофоном прервалась. " + error.Message;
                App.LogError("Виртуальный микрофон", error);
            }));
            _microphonePipe = pipe;
            pipe.Start();
            _enabledMicrophoneGuid = input.Guid;
            SaveAudioProfile();
            ApoInputStatusText.Text = $"В Discord выберите вход «{cableOutput.Name}».";
        }
        catch (Exception ex)
        {
            StopMicrophonePipe();
            _enabledMicrophoneGuid = null;
            SaveAudioProfile();
            ApoInputStatusText.Text = ex.Message;
            App.LogError("Запуск виртуального микрофона", ex);
        }
    }

    private void StopMicrophonePipe()
    {
        var pipe = _microphonePipe;
        _microphonePipe = null;
        try { pipe?.Dispose(); }
        catch (Exception ex) { App.LogError("Остановка виртуального микрофона", ex); }
    }

    private async Task RestoreCaptureAsync(ApoBridge.Installation installation, string guid)
    {
        ApoInputStatusText.Text = "Восстанавливаю штатную цепочку записи Windows…";
        try { await ApoBridge.RemoveCaptureAsync(installation, guid); }
        finally { ApoBridge.DisableCaptureEnhancements(guid); }
        if (_managedCaptureGuid == guid) _managedCaptureGuid = null;
        SaveAudioProfile();
    }

    private void ApplyAudioProfiles(ApoBridge.Installation installation, AudioEndpoints.Endpoint? output)
    {
        var playback = output is null ? null : new ApoProfiles.Settings(
            output.Guid, _outputEq, OutputGain.Value, OutputCompression.Value, OutputReverb.Value);
        ApoBridge.Apply(installation, playback);
    }

    private async Task<(string Key, string Status)?> ConnectOutputEndpointAsync(
        ApoBridge.Installation installation, AudioEndpoints.Endpoint? endpoint, (string Key, string Status)? previous)
    {
        var label = ApoStatusText;
        if (endpoint is null)
        {
            label.Text = "Выбранное устройство воспроизведения недоступно.";
            return null;
        }

        string key = Path.Combine(installation.InstallDir, endpoint.Guid);
        if (previous is { } connected && connected.Key == key)
        {
            label.Text = connected.Status;
            return connected;
        }

        label.Text = "Windows подключает и проверяет устройство…";
        string status;
        try
        {
            await ApoBridge.EnsurePlaybackAsync(installation, endpoint.Guid);
            status = "Обработка подключена к этому устройству.";
        }
        catch (Exception ex)
        {
            status = "Не удалось подключить устройство. " + ex.Message;
            App.LogError($"Подключение аудиоустройства {endpoint.Name} ({endpoint.Guid})", ex);
        }
        if (!_closing && _audioEnabled && Equals(endpoint, OutputDevices.SelectedItem)) label.Text = status;
        return (key, status);
    }

    private void DisconnectGlobalApo_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _audioEnabled = false;
            _testCaptureRequested = false;
            _enabledMicrophoneGuid = null;
            StopMicrophonePipe();
            ApoStatusText.Text = ApoInputStatusText.Text = "Обработка PFF отключена.";
            SaveAudioProfile();
            SynchronizeAudio();
        }
        catch (Exception ex)
        {
            ApoStatusText.Text = ApoInputStatusText.Text = "Ошибка отключения APO. " + ex.Message;
            App.LogError("Отключение Equalizer APO", ex);
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_synchronizingAudio)
        {
            _closeAfterAudio = true;
            e.Cancel = true;
            return;
        }
        SaveSettings();
        _closing = true;
        bool pendingAudio = _audioSaveTimer.IsEnabled;
        _audioSaveTimer.Stop();
        SaveAudioProfile();
        if ((pendingAudio || _audioSyncQueued) && _audioEnabled)
        {
            try
            {
                if (ApoBridge.Find() is { SupportsAutomaticRouting: true } installation)
                {
                    ApplyAudioProfiles(installation, OutputDevices.SelectedItem as AudioEndpoints.Endpoint);
                }
            }
            catch (Exception ex) { App.LogError("Сохранение обработки звука при закрытии", ex); }
        }

        StopMicrophonePipe();
        _uiTimer.Stop(); _metroTimer.Stop();
        base.OnClosing(e);
    }
}
