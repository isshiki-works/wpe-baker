using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Baker.Core;
using Microsoft.Win32;

namespace Baker.App;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<JobItem> jobs = [];
    private NativeTools? tools;
    private JsonObject? hybridPlan;
    private string setupError = "";
    // 工具缺失是独立一条：setupError 是单字段五处写入，后写者赢，GPU 或源属性的错误会把"生成工具未就绪"
    // 顶掉，界面就会指着错误的原因（实测显示 No Vulkan device found，而真因是 tools 为 null）。
    private string toolsError = "";
    private bool initialized, english, analyzing, processing, closeRequested, detecting, updatingInstallation, audioEffectsChoiceKnown, presetBusy, suppressSettingsChanges;
    // 高级区调速预算框里上一次由档位写进去的文本：与框里的内容一致就说明用户没自己填，换档时跟着换。
    private string presetBudgetText = "";
    private int settingsRevision;
    private CancellationTokenSource? analysisCancellation, runCancellation;
    private JobItem? activeJob;
    private Task queueRun = Task.CompletedTask;
    private JsonObject analysisPreviewOverrides = new();
    private JsonObject sourcePropertyDefinitions = new();
    // 当前来源在 Wallpaper Engine 里的属性设置（只读 config.json）；面板预填与分析时的属性底值都取它。
    private WallpaperEngineProperties.Resolution? sourceWpeProperties;
    // 启动时按 OutputFrameRate 算出的默认帧率与依据：帧率框的预填值，框里没改过时也作为 plan 的 frame_rate 来源。
    private OutputFrameRate.Choice? autoFrameRate;
    private JsonArray? layerList;
    private readonly HashSet<int> excludedLayerIds = [];
    // 取舍卡片上勾着的项；每次新的分析结果出来都按"最有希望的那一档"重置，用户自己改过就以他的勾选为准。
    private readonly HashSet<string> selectedTurnOffKinds = new(StringComparer.Ordinal);
    private bool turnOffKindsChosen;
    private readonly Dictionary<FrameworkElement, string> bilingualToolTips = [];
    private readonly string officialPreviewName = "WPE Baker Preview " + Guid.NewGuid().ToString("N");
    private string? officialPreviewExecutable;
    private string defaultOutputDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "WPE Baker");
    private string L(string zh, string en) => english ? en : zh;

    public MainWindow()
    {
        InitializeComponent();
        QueueList.ItemsSource = jobs;
        OutputBox.Text = defaultOutputDirectory;
        // 高级区的调速预算开局显示默认档（平衡）的值，用户改过才算覆盖。
        presetBudgetText = PresetBudgetText();
        RetimeBudgetBox.Text = presetBudgetText;
        // 摆动改频默认勾上，与 CLI 的 --sway-retime 同一个默认值；取消勾选才关掉它。
        SwayRetimeBox.IsChecked = SwayRetimeOptions.OnByDefault;
        try
        {
            UpdateInstallationDefaults(AppEnvironment.FindWallpaperExecutable());
            if (AssetsBox.Text.Length == 0) AssetsBox.Text = AppEnvironment.FindAssets();
            // 帧率默认值与 CLI 同一处逻辑（OutputFrameRate）：min(WPE 帧率上限, 主屏刷新率) 就近取标准档，读不到回退 60。
            autoFrameRate = OutputFrameRate.Choose(0,
                () => WallpaperEngineProperties.ReadFrameRateLimit(WallpaperEngineProperties.LocateConfig(WpeExeBox.Text.Trim())),
                OutputFrameRate.PrimaryDisplayRefreshHz);
            FpsBox.Text = autoFrameRate.Fps.ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception error) { setupError = error.Message; }
        try { tools = AppEnvironment.FindTools(); }
        catch (Exception error) { setupError = toolsError = error.Message; }
        initialized = true;
        // 默认界面语言跟随系统（与 CLI 的 --lang 默认值同一处逻辑），不再写死中文。
        SetLanguage(Messages.DefaultLanguage() != Messages.Chinese);
        StatusText.Text = L("未选择壁纸。选择壁纸后执行分析。", "No wallpaper selected. Select a wallpaper, then run analysis.");
    }

    private async void WindowLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var devices = await Task.Run(VulkanDevices.Enumerate);
            GpuBox.ItemsSource = devices;
            GpuBox.SelectedItem = devices.FirstOrDefault(d => d.DeviceType == VulkanDeviceType.DiscreteGpu) ?? devices.FirstOrDefault();
            if (devices.Count == 0) setupError = "No Vulkan device found.";
        }
        catch (Exception error) { setupError = error.Message; }
        RefreshControls();
        if (File.Exists(WpeExeBox.Text)) await LoadTargetsAsync(autoImport: true);
    }

    internal void SetLanguage(bool useEnglish)
    {
        english = useEnglish;
        // 导入与自检的报错由 Baker.Core 产出，跟着界面语言一起切。
        AppEnvironment.Language = useEnglish ? Messages.English : Messages.Chinese;
        LanguageBox.SelectedIndex = useEnglish ? 1 : 0;
        void Translate(DependencyObject parent)
        {
            if (parent is FrameworkElement element)
            {
                if (element.Tag is string pair && pair.Contains('|'))
                {
                    string text = pair.Split('|')[useEnglish ? 1 : 0];
                    if (element is HeaderedContentControl header) header.Header = text;
                    else if (element is TextBlock block) block.Text = text;
                    else if (element is ContentControl content) content.Content = text;
                }
                // 提示气泡用 ‖ 分隔中英文；首次翻译时记下原串，之后按当前语言替换。
                if (element.ToolTip is string tip && tip.Contains('‖')) bilingualToolTips[element] = tip;
                if (bilingualToolTips.TryGetValue(element, out string? source))
                    element.ToolTip = source.Split('‖')[useEnglish ? 1 : 0];
            }
            foreach (object child in LogicalTreeHelper.GetChildren(parent))
                if (child is DependencyObject dependency) Translate(dependency);
        }
        Translate((DependencyObject)Content);
        foreach (var job in jobs) job.Translate(useEnglish);
        UpdatePlanSummary();
        BuildPropertyEditors();
        RefreshControls();
        if (!processing && !analyzing) StatusText.Text = hybridPlan is null
            ? L("未选择壁纸。选择壁纸后执行分析。", "No wallpaper selected. Select a wallpaper, then run analysis.")
            // 这张到底能不能做，结论区已经写得很清楚了，状态栏别在这里再下一次结论。
            : L("分析完成：结论见左侧结论区。", "Analysis complete; the verdict is in the panel on the left.");
    }

    private void LanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (initialized && english != (LanguageBox.SelectedIndex == 1)) SetLanguage(LanguageBox.SelectedIndex == 1);
    }

    private void ChooseFolder(TextBox target)
    {
        var dialog = new OpenFolderDialog { Multiselect = false, Title = L("选择文件夹", "Choose folder") };
        if (Directory.Exists(target.Text)) dialog.InitialDirectory = target.Text;
        if (dialog.ShowDialog(this) == true) target.Text = dialog.FolderName;
    }
    private void ChooseSource(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Multiselect = false, Title = L("选择壁纸文件夹", "Choose the wallpaper folder") };
        if (Directory.Exists(SourceBox.Text)) dialog.InitialDirectory = SourceBox.Text;
        if (dialog.ShowDialog(this) == true) ImportSource(dialog.FolderName);
    }
    private void ChooseSourceFile(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Multiselect = false, CheckFileExists = true,
            Title = L("选择壁纸文件", "Choose the wallpaper file"),
            Filter = "Scene wallpapers|*.json;*.pkg" };
        string? directory = Directory.Exists(SourceBox.Text) ? SourceBox.Text :
            File.Exists(SourceBox.Text) ? Path.GetDirectoryName(SourceBox.Text) : null;
        if (Directory.Exists(directory)) dialog.InitialDirectory = directory;
        if (dialog.ShowDialog(this) == true) ImportSource(dialog.FileName);
    }
    private void ChooseAssets(object sender, RoutedEventArgs e) => ChooseFolder(AssetsBox);
    private void ChooseOutput(object sender, RoutedEventArgs e) => ChooseFolder(OutputBox);
    private void SourceDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Effects = e.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } paths &&
            AppEnvironment.DropTarget(paths[0]) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }
    private void SourceDropped(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } paths) ImportSource(paths[0]);
        else StatusText.Text = L("一次只接受一个壁纸来源。", "One wallpaper source at a time.");
        e.Handled = true;
    }
    private bool ImportSource(string path)
    {
        try
        {
            SourceBox.Text = AppEnvironment.ValidateSource(path);
            StatusText.Text = L("壁纸来源已载入。可执行分析。", "Wallpaper source loaded. Ready to analyze.");
            return true;
        }
        // 报错只给人话：Baker.Core 现在给的是完整的一句话（SourceDiagnosis），异常类名对用户没有意义，
        // 它仍然写进 %LOCALAPPDATA%\WpeBaker\Logs 里的错误文件（见 SaveErrorAsync）。以下几处同理。
        catch (Exception error)
        {
            StatusText.Text = L("无法打开来源：", "Cannot open source: ") + error.Message;
            return false;
        }
    }
    private void ReloadSourcePropertyDefinitions(string sourcePath)
    {
        sourcePropertyDefinitions = new();
        sourceWpeProperties = null;
        if (!AppEnvironment.SourceExists(sourcePath)) return;
        try
        {
            using var source = new ProjectSource(sourcePath);
            sourcePropertyDefinitions = AppJsonPresentation.LoadPropertyDefinitions(source);
            sourceWpeProperties = AppJsonPresentation.ResolveWpeProperties(source, WpeExeBox.Text.Trim());
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            setupError = error.Message;
        }
    }
    private void SourceChanged(object sender, TextChangedEventArgs e)
    {
        if (!initialized || suppressSettingsChanges) return;
        ++settingsRevision;
        analysisCancellation?.Cancel();
        if (sender == SourceBox)
        {
            audioEffectsChoiceKnown = false;
            AudioEffectsBox.IsChecked = false;
            if (CurrentWallpaperBox.SelectedItem is CurrentWallpaperItem current &&
                !string.Equals(current.Source, SourceBox.Text.Trim(), StringComparison.OrdinalIgnoreCase))
                CurrentWallpaperBox.SelectedIndex = -1;
            QueueList.SelectedItem = null;
            ReloadSourcePropertyDefinitions(SourceBox.Text.Trim());
        }
        if (sender == AssetsBox && !updatingInstallation && AppEnvironment.AssetsValid(AssetsBox.Text))
        {
            string detected = AppEnvironment.FindWallpaperExecutable(AssetsBox.Text);
            if (detected.Length > 0) WpeExeBox.Text = detected;
        }
        hybridPlan = null;
        if (sender == SourceBox) { analysisPreviewOverrides = new(); layerList = null; excludedLayerIds.Clear(); }
        BuildPropertyEditors();
        UpdatePlanSummary(); RefreshControls();
    }
    private void SettingsChanged(object sender, RoutedEventArgs e)
    {
        if (!initialized || suppressSettingsChanges) return;
        ++settingsRevision;
        if (sender == OutputBox || sender == TargetBox) { RefreshControls(); return; }
        if (sender != AudioEffectsBox) audioEffectsChoiceKnown = false;
        analysisCancellation?.Cancel(); hybridPlan = null;
        UpdatePlanSummary(); RefreshControls();
    }
    private void FpsEdited(object sender, KeyEventArgs e) => SettingsChanged(sender, e);
    private void QueueSelectionChanged(object sender, SelectionChangedEventArgs e) { if (initialized) BuildPropertyEditors(); RefreshControls(); }

    /// <summary>下拉里选中的播放版编码档位；只影响播放版，无损母版永远是软件编码。</summary>
    private string SelectedPlaybackEncoder() =>
        PlaybackEncoderSelection.Normalize((EncoderBox.SelectedItem as ComboBoxItem)?.Content as string);

    /// <summary>画面尺寸两格：都留空得 0×0（分析时按场景画布取值），都填正整数得指定尺寸，其余视为无效。</summary>
    private bool TryFrameSize(out uint width, out uint height)
    {
        string widthText = WidthBox.Text.Trim(), heightText = HeightBox.Text.Trim();
        width = height = 0;
        if (widthText.Length == 0 && heightText.Length == 0) return true;
        return uint.TryParse(widthText, NumberStyles.None, CultureInfo.InvariantCulture, out width) && width > 0 &&
            uint.TryParse(heightText, NumberStyles.None, CultureInfo.InvariantCulture, out height) && height > 0;
    }

    private PresetSettings CurrentPresetSettings()
    {
        if (!TryFrameSize(out uint width, out uint height))
            throw new InvalidDataException("Frame width and height must both be positive integers, or both empty to use the scene canvas size.");
        return new(width, height, FpsBox.Text.Trim(),
            (GpuBox.SelectedItem as VulkanDeviceInfo)?.DeviceUuid, RetimeBox.IsChecked == true, FixedViewBox.IsChecked == true,
            LayeredVideoBox.IsChecked == true, ForegroundLiveBox.IsChecked == true, SimpleTextEffectsBox.IsChecked == true,
            AudioEffectsBox.IsChecked == true, SelectedLoopPreference());
    }

    // 档位下拉按顺序对应 efficiency / balanced / quality，默认平衡。
    private string SelectedPreset() => PresetBox.SelectedIndex switch
    {
        0 => RetimeProfile.Efficiency, 2 => RetimeProfile.Quality, _ => RetimeProfile.Balanced
    };

    // 循环取向跟着档位走：效率＝周期最短，质量＝调速最少，平衡居中。界面上不再单独给这个选项。
    private string SelectedLoopPreference() => PresetBox.SelectedIndex switch
    {
        0 => "performance", 2 => "quality", _ => "balanced"
    };

    /// <summary>当前档位的调速预算文本（质量档没有百分比门槛，留空）。</summary>
    private string PresetBudgetText() => RetimeProfile.PresetBudgetPercent(SelectedPreset()) is double percent
        ? percent.ToString("0.###", CultureInfo.InvariantCulture) : "";

    /// <summary>调速预算框里还是档位写进去的那个值（含质量档的空）：没被用户改过，就不往请求里带覆盖。</summary>
    private bool RetimeBudgetFollowsPreset => RetimeBudgetBox.Text.Trim() == presetBudgetText;

    /// <summary>用户自己填的调速预算（0 到 5）才算覆盖；跟着档位或填了非法值时不带覆盖，校验行会提示。</summary>
    private double? RetimeBudgetOverride() =>
        !RetimeBudgetFollowsPreset &&
        double.TryParse(RetimeBudgetBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double percent) &&
        double.IsFinite(percent) && percent >= 0 && percent <= RetimeProfile.MaximumBudgetPercent ? percent : null;

    /// <summary>档位换了：高级区的调速预算跟着换成该档的值，用户自己填过的（与上一档的值不同）保持不动。</summary>
    private void PresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized) return;
        if (RetimeBudgetBox.Text.Trim() == presetBudgetText)
        {
            suppressSettingsChanges = true;
            RetimeBudgetBox.Text = PresetBudgetText();
            suppressSettingsChanges = false;
        }
        presetBudgetText = PresetBudgetText();
        SettingsChanged(sender, e);
    }

    private void RetimeBudgetEdited(object sender, RoutedEventArgs e) => SettingsChanged(sender, e);

    /// <summary>三个选择各是什么意思，以及高级区那格"跟着选择走 / 按你填的"标记；每次设置变化都刷一遍。</summary>
    private void UpdatePresetNote()
    {
        PresetNote.Text = PresetBox.SelectedIndex switch
        {
            0 => L("速度优先，画面差异较大。", "Speed first; larger visual difference."),
            2 => L("画面差异最小，耗时更长、文件更大。", "Smallest visual difference; longer runtime and larger file."),
            _ => L("默认：速度与画面差异平衡。", "Default: balanced speed and visual difference.")
        };
        RetimeBudgetOrigin.Text = RetimeBudgetFollowsPreset
            ? L("取自档位", "from the selected profile")
            : RetimeBudgetOverride() is null ? L("无效值，范围 0 到 5", "invalid value, range 0 to 5")
            : L("手动覆盖", "manual override");
    }

    private async void SavePresetClicked(object sender, RoutedEventArgs e)
    {
        if (presetBusy) return;
        var dialog = new SaveFileDialog
        {
            Title = L("保存当前壁纸方案", "Save current wallpaper preset"),
            Filter = "WPE Baker preset|*.wpebaker.json|JSON|*.json", DefaultExt = ".wpebaker.json",
            AddExtension = true, FileName = "settings.wpebaker.json"
        };
        if (dialog.ShowDialog(this) != true) return;
        presetBusy = true; RefreshControls();
        try
        {
            string sourcePath = SourceBox.Text.Trim();
            if (!AppEnvironment.OutputValid(Path.GetDirectoryName(Path.GetFullPath(dialog.FileName))!, sourcePath))
                throw new InvalidDataException(L("方案不能保存在壁纸来源文件夹内。", "Presets cannot be saved inside the wallpaper source folder."));
            PresetSettings settings = CurrentPresetSettings();
            JsonObject definitions = sourcePropertyDefinitions.DeepClone().AsObject();
            // 方案存的是面板上实际生效的值（WPE 设置 + 面板改动），换机器或之后改了 WPE 设置也能原样恢复。
            JsonObject properties = AppJsonPresentation.MergeWpeProperties(sourceWpeProperties, definitions, analysisPreviewOverrides).Properties;
            int revision = settingsRevision;
            string? planHash = hybridPlan?["source_sha256"]?.GetValue<string>();
            bool matchingPlan = planHash is not null && string.Equals(hybridPlan?["source"]?.GetValue<string>(), sourcePath, StringComparison.OrdinalIgnoreCase);
            string sourceHash = matchingPlan ? planHash! : await Task.Run(async () =>
            {
                using var source = new ProjectSource(sourcePath);
                return await source.SourceHashAsync();
            });
            if (revision != settingsRevision || !string.Equals(sourcePath, SourceBox.Text.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                StatusText.Text = L("保存期间设置已变更，未写入方案。", "Settings changed while saving; no preset was written.");
                return;
            }
            JsonObject preset = SettingsPreset.Create(sourcePath, sourceHash, settings, properties);
            _ = SettingsPreset.Parse(preset, definitions);
            File.WriteAllText(dialog.FileName, preset.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            StatusText.Text = L("方案已保存。", "Preset saved.");
        }
        catch (Exception error) { StatusText.Text = L("方案保存失败：", "Preset save failed: ") + error.Message; }
        finally { presetBusy = false; RefreshControls(); }
    }

    private async void LoadPresetClicked(object sender, RoutedEventArgs e)
    {
        if (analyzing || processing || presetBusy || QueueList.SelectedItem is not null) return;
        var dialog = new OpenFileDialog
        {
            Title = L("载入壁纸方案", "Load wallpaper preset"),
            Filter = "WPE Baker preset|*.wpebaker.json|JSON|*.json", CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;
        string sourcePath = SourceBox.Text.Trim();
        int revision = settingsRevision;
        presetBusy = true; RefreshControls();
        try
        {
            JsonObject document = JsonNode.Parse(await File.ReadAllTextAsync(dialog.FileName))?.AsObject()
                ?? throw new InvalidDataException("Preset is not a JSON object.");
            var sourceState = await Task.Run(async () =>
            {
                using var source = new ProjectSource(sourcePath);
                return (Definitions: AppJsonPresentation.LoadPropertyDefinitions(source), Hash: await source.SourceHashAsync());
            });
            if (revision != settingsRevision || !string.Equals(sourcePath, SourceBox.Text.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                StatusText.Text = L("载入期间设置已变更，未应用方案。", "Settings changed while loading; preset was not applied.");
                return;
            }
            JsonObject definitions = sourceState.Definitions;
            SettingsPreset preset = SettingsPreset.Parse(document, definitions);
            if (!string.Equals(sourceState.Hash, preset.SourceSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Preset belongs to a different wallpaper source.");
            VulkanDeviceInfo? gpu = preset.Settings.DeviceUuid is null ? null : GpuBox.Items.OfType<VulkanDeviceInfo>()
                .FirstOrDefault(device => device.DeviceUuid.Equals(preset.Settings.DeviceUuid, StringComparison.OrdinalIgnoreCase));
            if (preset.Settings.DeviceUuid is not null && gpu is null) throw new InvalidDataException("Preset render GPU is not available.");

            suppressSettingsChanges = true;
            try
            {
                sourcePropertyDefinitions = definitions;
                analysisPreviewOverrides = preset.Properties.DeepClone().AsObject();
                // 0×0 是"按场景画布"，界面上表现为两格留空。
                WidthBox.Text = preset.Settings.Width == 0 ? "" : preset.Settings.Width.ToString(CultureInfo.InvariantCulture);
                HeightBox.Text = preset.Settings.Height == 0 ? "" : preset.Settings.Height.ToString(CultureInfo.InvariantCulture);
                FpsBox.Text = preset.Settings.Fps;
                GpuBox.SelectedItem = gpu;
                RetimeBox.IsChecked = preset.Settings.Retime; FixedViewBox.IsChecked = preset.Settings.FixedView;
                LayeredVideoBox.IsChecked = preset.Settings.LayeredVideo; ForegroundLiveBox.IsChecked = preset.Settings.ForegroundLive;
                SimpleTextEffectsBox.IsChecked = preset.Settings.SimpleTextEffects; AudioEffectsBox.IsChecked = preset.Settings.AudioEffects;
                // 方案文件里的 loop_preference 就是档位（两者一一对应）；调速预算回到该档的值。
                PresetBox.SelectedIndex = preset.Settings.LoopPreference switch { "performance" => 0, "quality" => 2, _ => 1 };
                presetBudgetText = PresetBudgetText();
                RetimeBudgetBox.Text = presetBudgetText;
            }
            finally { suppressSettingsChanges = false; }
            audioEffectsChoiceKnown = false;
            analysisCancellation?.Cancel(); hybridPlan = null;
            ++settingsRevision;
            BuildPropertyEditors(); UpdatePlanSummary(); RefreshControls();
            StatusText.Text = L("方案已载入。生成前需重新分析。", "Preset loaded. Re-analysis required before generating.");
        }
        catch (Exception error) { StatusText.Text = L("方案载入失败：", "Preset load failed: ") + error.Message; }
        finally { presetBusy = false; RefreshControls(); }
    }

    private async void AnalyzeClicked(object sender, RoutedEventArgs e)
    {
        string source = SourceBox.Text.Trim(), assets = AssetsBox.Text.Trim();
        analysisCancellation?.Dispose();
        analysisCancellation = new CancellationTokenSource();
        analyzing = true;
        StatusText.Text = L("正在分析…", "Analyzing…");
        RefreshControls();
        try
        {
            // 工具缺失时报工具自己的错，不报后写进 setupError 的 GPU 或源属性错误。
            if (tools is null) throw new InvalidOperationException(toolsError.Length > 0 ? toolsError : setupError);
            ReloadSourcePropertyDefinitions(source);
            using var sourceView = new ProjectSource(source);
            if (sourceView.Kind != "scene") throw new InvalidDataException("Hybrid baking requires a Scene project.");
            if (!AppEnvironment.TryFrameRate(FpsBox.Text, out uint numerator, out uint denominator)) throw new InvalidDataException("Invalid frame rate.");
            var gpu = GpuBox.SelectedItem as VulkanDeviceInfo;
            string output = Path.Combine(Path.GetTempPath(), "WpeBaker", "analysis-" + Guid.NewGuid().ToString("N"));
            if (!TryFrameSize(out uint width, out uint height))
                throw new InvalidDataException("Frame width and height must both be positive integers, or both empty to use the scene canvas size.");
            // 属性底值是用户在 Wallpaper Engine 里的设置，面板里的改动覆盖在上；来源记录写进 plan。
            var (properties, propertiesOrigin) = AppJsonPresentation.MergeWpeProperties(sourceWpeProperties, sourcePropertyDefinitions, analysisPreviewOverrides);
            var request = new HybridAnalyzeRequest(2, source, assets, output, width, height, numerator, denominator,
                properties, FixedViewBox.IsChecked == true ? "fixed_view" : "preserve", RetimeBox.IsChecked == true ? 2 : 0,
                AllowLocalSeamRepair: false, DeviceUuid: gpu?.DeviceUuid,
                VideoLayout: LayeredVideoBox.IsChecked == true ? "layered" : "full_frame",
                LiveOverlayPlacement: ForegroundLiveBox.IsChecked == true ? "foreground" : "preserve",
                LiveTextEffects: SimpleTextEffectsBox.IsChecked == true ? "simple" : "preserve",
                AudioEffects: AudioEffectsBox.IsChecked == true ? "omit" : "preserve",
                ExcludedLayerIds: excludedLayerIds.Count == 0 ? null : excludedLayerIds.Order().ToArray(),
                LoopPreference: SelectedLoopPreference(),
                PropertiesOrigin: propertiesOrigin,
                // 帧率框还是启动时算出的默认值就记 auto（连同依据），用户改过就记 explicit。
                FrameRateOrigin: (autoFrameRate is { } automatic && denominator == 1 && numerator == automatic.Fps
                    ? automatic : OutputFrameRate.Requested(numerator)).ToJson(),
                // 档位与高级区的覆盖：界面上选的档走同一条 RetimeProfile 路径，与 CLI 的 --preset 一致。
                Preset: SelectedPreset(),
                RetimeBudgetPercent: RetimeBudgetOverride(),
                // 摆动改频三档都开（设计 §3），高级区的勾选框默认勾上，取消勾选才关；与 CLI 的 --sway-retime 默认一致。
                SwayRetime: SwayRetimeBox.IsChecked == true);
            // 分析前先量一遍原来这张现在费多少电（默认开）：读数写进 plan 的 source_power，结论第一行按实测分档；
            // 量不了的机器跳过并把原因记进 plan，分析照常进行。
            JsonObject? sourcePower = MeasureSourceBox.IsChecked == true
                ? await MeasureSourcePowerAsync(source, SourcePowerVerdict.SampleDirectory(output),
                    numerator / (double)denominator, analysisCancellation.Token)
                : null;
            StatusText.Text = L("正在分析…", "Analyzing…");
            var found = await Task.Run(() => new HybridScenePlanner(tools).AnalyzeAsync(request, null, analysisCancellation.Token));
            if (sourcePower is not null) SourcePowerVerdict.Apply(found, sourcePower);
            analysisCancellation.Token.ThrowIfCancellationRequested();
            if (SourceBox.Text.Trim() == source && AssetsBox.Text.Trim() == assets)
            {
                hybridPlan = found;
                audioEffectsChoiceKnown = AppJsonPresentation.HasAudioEffectsChoice(found);
                // 新结果出来了，勾选回到推荐的那一档。
                turnOffKindsChosen = false;
                UpdatePlanSummary(); BuildPropertyEditors(); RefreshControls();
                StatusText.Text = found["blockers"] is JsonArray { Count: > 0 }
                    ? L("分析完成：不可生成，原因见结论区。", "Analysis complete: cannot generate; see the verdict above.")
                    : L("分析完成：可生成。", "Analysis complete: ready to generate.");
            }
        }
        catch (OperationCanceledException) { StatusText.Text = L("来源已变更，上一次分析已取消。", "Source changed; the previous analysis was cancelled."); }
        catch (Exception error) { StatusText.Text = L("分析失败：", "Analysis failed: ") + error.Message; }
        finally { analyzing = false; RefreshControls(); }
    }

    /// <summary>
    /// 结论区：第一行一句话结论、第二行几个数字、第三行"详情"折叠面板（里面是原来那段技术文本，一句没删），
    /// 再下面是可勾选的取舍卡片。
    /// </summary>
    private void UpdatePlanSummary()
    {
        AudioEffectsBox.Visibility = audioEffectsChoiceKnown ? Visibility.Visible : Visibility.Collapsed;
        if (hybridPlan?["layers"] is JsonArray analyzedLayers) layerList = analyzedLayers.DeepClone().AsArray();
        BuildLayerList();
        UpdateOutputSummary();
        VerdictLine.Text = hybridPlan is null
            ? L("未评估", "Not evaluated")
            : PlainLanguage.Verdict(hybridPlan, english);
        NumbersLine.Text = hybridPlan is null
            ? L("选择壁纸后执行分析", "Select a wallpaper, then run analysis")
            : PlainLanguage.NextAction(hybridPlan, english);
        NumbersLine.Visibility = NumbersLine.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        DetailsExpander.Visibility = hybridPlan is null ? Visibility.Collapsed : Visibility.Visible;
        // 依据（功耗读数、路线、拒绝原因）与数字都不进结论前两行，作为"详情"面板的第一段。
        PlanSummary.Text = string.Join("\n", new[] {
            PlainLanguage.Basis(hybridPlan, english), PlainLanguage.Numbers(hybridPlan, english),
            AppJsonPresentation.RouteSummary(hybridPlan, english) }.Where(part => part.Length > 0));
        LiveFeatures.Text = AppJsonPresentation.PlanNotes(hybridPlan, english);
        LiveFeatures.Visibility = LiveFeatures.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        ProfileLine.Text = AppJsonPresentation.ProfileSummary(hybridPlan, english);
        ProfileLine.Visibility = ProfileLine.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        BuildTradeoffList();
        BuildTurnOffCard();
        UpdatePresetNote();
    }

    /// <summary>输出那一行：帧率与画面大小；没自己填尺寸时写"自动"，分析完就把实际出片尺寸摆上去。</summary>
    private void UpdateOutputSummary()
    {
        string fps = FpsBox.Text.Trim();
        var parts = new List<string> { fps.Length == 0 ? L("帧率自动", "frame rate automatic") : fps + " fps" };
        if (TryFrameSize(out uint width, out uint height) && width > 0 && height > 0)
            parts.Add(width.ToString(CultureInfo.InvariantCulture) + "×" + height.ToString(CultureInfo.InvariantCulture));
        else
        {
            double analyzedWidth = AppJsonPresentation.Number(hybridPlan?["output_resolution"]?["width"]) ?? 0;
            double analyzedHeight = AppJsonPresentation.Number(hybridPlan?["output_resolution"]?["height"]) ?? 0;
            if (analyzedWidth > 0 && analyzedHeight > 0)
                parts.Add(analyzedWidth.ToString("0", CultureInfo.InvariantCulture) + "×" + analyzedHeight.ToString("0", CultureInfo.InvariantCulture));
            parts.Add(L("自动", "automatic"));
        }
        OutputSummary.Text = string.Join(" · ", parts);
    }

    /// <summary>点铅笔展开／收起帧率和画面大小两格。</summary>
    private void ToggleOutputEditor(object sender, RoutedEventArgs e) =>
        OutputEditor.Visibility = OutputEditor.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>
    /// 取舍卡片：一项一个勾选框，写清楚关掉它画面上会少什么。只列这张壁纸上真有、且关得掉的东西；
    /// 默认勾上"最有希望的那一档"。画面本身就是实时特效画出来的那类壁纸不出卡片，只留第一行结论。
    /// </summary>
    private void BuildTurnOffCard()
    {
        TurnOffList.Children.Clear();
        var items = PlainLanguage.TurnOffItems(hybridPlan, english);
        TurnOffCard.Visibility = items.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (items.Length == 0)
        {
            selectedTurnOffKinds.Clear();
            UpdateTurnOffNote();
            return;
        }
        if (!turnOffKindsChosen)
        {
            selectedTurnOffKinds.Clear();
            foreach (var item in items.Where(item => item.Recommended)) selectedTurnOffKinds.Add(item.Kind);
        }
        foreach (var item in items)
        {
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = L("禁用 ", "Disable ") + item.Label,
                TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.Normal });
            text.Children.Add(new TextBlock { Text = item.Consequence, FontSize = 11, TextWrapping = TextWrapping.Wrap,
                Foreground = (System.Windows.Media.Brush)FindResource("Hint"), Margin = new Thickness(0, 2, 0, 0) });
            var box = new CheckBox { IsChecked = selectedTurnOffKinds.Contains(item.Kind), Content = text,
                Padding = new Thickness(6, 0, 0, 0), Margin = new Thickness(0, 0, 8, 10) };
            string kind = item.Kind;
            box.Checked += (_, _) => ToggleTurnOff(kind, true);
            box.Unchecked += (_, _) => ToggleTurnOff(kind, false);
            TurnOffList.Children.Add(box);
        }
        UpdateTurnOffNote();
    }

    private void ToggleTurnOff(string kind, bool off)
    {
        if (!(off ? selectedTurnOffKinds.Add(kind) : selectedTurnOffKinds.Remove(kind))) return;
        turnOffKindsChosen = true;
        UpdateTurnOffNote();
    }

    /// <summary>卡片底部那行小字与两个按钮：跟着勾选走，勾什么就按哪套方案算还剩多少实时效果。</summary>
    private void UpdateTurnOffNote()
    {
        var option = CurrentTurnOffOption();
        ResidualNote.Text = PlainLanguage.ResidualNote(option, english);
        ApplyTurnOffButton.IsEnabled = option is not null && !analyzing && !presetBusy;
        CopyForDeveloperButton.IsEnabled = option is not null;
    }

    private AppJsonPresentation.AppTradeoffOption? CurrentTurnOffOption() =>
        PlainLanguage.Match(AppJsonPresentation.TradeoffOptionViews(hybridPlan, english), selectedTurnOffKinds);

    private void ApplyTurnOffClicked(object sender, RoutedEventArgs e)
    {
        if (CurrentTurnOffOption() is { } option) ApplyTradeoffOption(option);
    }

    /// <summary>把当前这套方案的命令行和技术说明一起复制走，方便贴给开发者。</summary>
    private void CopyForDeveloperClicked(object sender, RoutedEventArgs e)
    {
        if (CurrentTurnOffOption() is not { } option) return;
        string text = string.Join(Environment.NewLine, new[] { option.Title }.Concat(option.Lines)
            .Concat(option.Command.Length == 0 ? [] : new[] { option.Command }).Where(line => line.Length > 0));
        CopyTradeoffCommand(text);
    }

    /// <summary>
    /// 结论下方的取舍清单：每个方案一块，块里按"要关什么 → 怎么关（属性优先、命令行其次）→ 连带关掉什么 →
    /// 关掉后的路线与残留实时层 → 保留实时不省电"排，再给"按此方案重新分析"和"复制命令行"两个按钮。
    /// 主体类壁纸（关掉就没有内容）只显示那句拒绝说明，不出清单。
    /// </summary>
    private void BuildTradeoffList()
    {
        TradeoffList.Children.Clear();
        TradeoffHeader.Text = AppJsonPresentation.TradeoffHeader(hybridPlan, english);
        TradeoffHeader.Visibility = TradeoffHeader.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        var options = AppJsonPresentation.TradeoffOptionViews(hybridPlan, english);
        TradeoffList.Visibility = options.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        foreach (var option in options)
        {
            var body = new StackPanel();
            body.Children.Add(new TextBlock { Text = option.Title, FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) });
            foreach (string line in option.Lines)
                body.Children.Add(new TextBlock { Text = line, FontSize = 12, TextWrapping = TextWrapping.Wrap,
                    Foreground = (System.Windows.Media.Brush)FindResource("Muted"), Margin = new Thickness(0, 0, 0, 3) });
            var buttons = new WrapPanel { Margin = new Thickness(0, 7, 0, 0) };
            var reanalyze = new Button { Content = L("按此方案重新分析", "Re-analyze with this option"), Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 0, 6, 0) };
            reanalyze.Click += (_, _) => ApplyTradeoffOption(option);
            buttons.Children.Add(reanalyze);
            if (option.Command.Length > 0)
            {
                var copy = new Button { Content = L("复制命令行", "Copy command line"), Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0) };
                copy.Click += (_, _) => CopyTradeoffCommand(option.Command);
                buttons.Children.Add(copy);
            }
            body.Children.Add(buttons);
            TradeoffList.Children.Add(new Border {
                BorderBrush = (System.Windows.Media.Brush)FindResource("Line"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7), Padding = new Thickness(11), Margin = new Thickness(0, 0, 0, 8), Child = body });
        }
    }

    /// <summary>把一个取舍方案填进当前设置（属性关闭值、排除图层、固定视角）并立刻重新分析。</summary>
    private void ApplyTradeoffOption(AppJsonPresentation.AppTradeoffOption option)
    {
        if (analyzing || presetBusy)
        {
            StatusText.Text = L("上一次分析未结束，操作已忽略。", "The previous analysis has not finished; the request was ignored.");
            return;
        }
        suppressSettingsChanges = true;
        try
        {
            foreach (var (key, offValue) in option.Properties) analysisPreviewOverrides[key] = offValue?.DeepClone();
            foreach (int id in option.ExcludeLayers) excludedLayerIds.Add(id);
            if (option.FixedView) FixedViewBox.IsChecked = true;
        }
        finally { suppressSettingsChanges = false; }
        ++settingsRevision;
        analysisCancellation?.Cancel(); hybridPlan = null;
        BuildPropertyEditors();
        UpdatePlanSummary(); RefreshControls();
        AnalyzeClicked(this, new RoutedEventArgs());
    }

    private void CopyTradeoffCommand(string command)
    {
        try
        {
            Clipboard.SetText(command);
            StatusText.Text = L("已复制到剪贴板。", "Copied to clipboard.");
        }
        catch (Exception error) { StatusText.Text = L("复制失败：", "Copy failed: ") + error.Message; }
    }

    private void RefreshControls()
    {
        if (!initialized) return;
        bool sourceValid = AppEnvironment.SourceExists(SourceBox.Text.Trim());
        bool assetsValid = AppEnvironment.AssetsValid(AssetsBox.Text.Trim());
        bool outputValid = AppEnvironment.OutputValid(OutputBox.Text.Trim(), SourceBox.Text.Trim());
        bool fpsValid = AppEnvironment.TryFrameRate(FpsBox.Text, out _, out _);
        bool gpuValid = GpuBox.SelectedItem is VulkanDeviceInfo;
        string hybridBlockers = string.Join("; ", AppJsonPresentation.BlockerLines(hybridPlan, english));
        bool hybridBlocked = hybridPlan?["status"]?.GetValue<string>() == "requires_resolution" || hybridBlockers.Length > 0;
        // 分析同样硬依赖生成工具（:429 会直接抛），所以工具缺失时按钮就不该可点——生成按钮本来就有这一条。
        AnalyzeButton.IsEnabled = sourceValid && assetsValid && tools is not null && !analyzing && !presetBusy;
        SavePresetButton.IsEnabled = sourceValid && fpsValid && TryFrameSize(out _, out _) && !analyzing && !processing && !presetBusy;
        LoadPresetButton.IsEnabled = sourceValid && !analyzing && !processing && !presetBusy && QueueList.SelectedItem is null;
        GenerateButton.IsEnabled = hybridPlan is not null && sourceValid && outputValid && fpsValid && assetsValid &&
            !hybridBlocked && tools is not null && gpuValid && !analyzing;
        // 工具缺失排在最前（fix/release-blockers）：它挡住分析与生成两条路，开窗时就该说清楚，而且用的是
        // 它自己的错误文本，不会被后写的 GPU 或源属性错误顶掉。文案按「界面说人话」那版，别回到术语。
        ValidationText.Text = tools is null ? L("生成工具未就绪：", "Generation tools not ready: ") +
                (toolsError.Length > 0 ? toolsError : setupError) :
            !sourceValid ? L("未选择壁纸来源。指定壁纸文件夹或文件。", "No wallpaper source selected. Specify a wallpaper folder or file.") :
            !assetsValid ? L("assets 路径无效。指定 Wallpaper Engine 的 assets 文件夹。", "Invalid assets path. Specify the Wallpaper Engine assets folder.") :
            !outputValid ? L("输出目录无效：不能位于壁纸来源文件夹内。", "Invalid output directory: it must be outside the wallpaper source folder.") :
            !fpsValid ? L("帧率无效：取值范围 1 到 1000 fps。", "Invalid frame rate: range 1 to 1000 fps.") :
            !gpuValid ? L("未找到可用的 Vulkan 设备。", "No usable Vulkan device found.") + (setupError.Length > 0 ? " " + setupError : "") :
            hybridPlan is null ? L("未评估：生成前需执行分析。", "Not evaluated: run analysis before generating.") :
            // 具体是哪条挡住了，结论区已经用人话写了，这里不再把 blocker 原文堆到状态行上。
            hybridBlocked ? L("不可生成：原因见结论区。", "Cannot generate: the reason is stated in the verdict above.") :
            L("将在输出目录生成新壁纸。", "A new wallpaper will be generated in the output directory.");
        // 能生成就用普通灰，被挡住就用红，一眼看出还差什么。
        ValidationText.Foreground = GenerateButton.IsEnabled ? StateBrushes.Muted : StateBrushes.Bad;
        var selected = QueueList.SelectedItem as JobItem;
        LoadResultButton.IsEnabled = !processing && tools is not null;
        CancelButton.IsEnabled = selected?.State == "queued" || (selected is not null && selected == activeJob);
        RetryButton.IsEnabled = selected is not null && selected.State is "completed" or "cancelled" or "failed";
        ResultButton.IsEnabled = selected is not null && Directory.Exists(selected.Request.OutputDirectory);
        ErrorButton.IsEnabled = selected?.ErrorPath is string error && File.Exists(error);
        OfficialPreviewButton.IsEnabled = selected?.State == "completed" && selected.ProjectPath is not null &&
            !processing && File.Exists(WpeExeBox.Text.Trim());
        OfficialPreviewButton.ToolTip = L("在独立 Wallpaper Engine 窗口中播放成品，不改变桌面壁纸。",
            "Plays the generated result in a separate Wallpaper Engine window; the desktop wallpaper is unchanged.");
        ValidateButton.IsEnabled = selected?.State == "completed" && !processing &&
            selected.ProjectPath is not null;
        ValidateButton.Content = L("查看自动检查结果", "Show automatic checks");
        ValidateButton.ToolTip = L("显示生成过程中记录的自动检查结果。", "Shows the checks recorded during generation.");
        MeasurePlaybackButton.IsEnabled = selected?.State == "completed" && !processing && File.Exists(WpeExeBox.Text.Trim());
        MeasurePlaybackButton.Content = L("实测当前播放功耗", "Measure current playback power");
        MeasurePlaybackButton.ToolTip = L("读取运行中 Wallpaper Engine 的功耗；同时采集帧率需管理员权限。", "Reads the running Wallpaper Engine power draw; sampling displayed frames requires administrator rights.");
        ExportButton.IsEnabled = selected?.State == "completed" && selected.ProjectPath is not null &&
            (!processing || selected.ExportArchive is not null);
        ExportButton.Content = selected?.ExportArchive is null ? L("导出 ZIP", "Export ZIP") : L("打开 ZIP", "Open ZIP");
        PropertiesEditor.IsEnabled = !processing && !analyzing && !presetBusy && selected is null;
        LayersExpander.IsEnabled = layerList is { Count: > 0 } && !processing && !analyzing && !presetBusy && selected is null;
        ReportButton.IsEnabled = selected?.LatestReportPath is string report && File.Exists(report);
        RefreshTargetsButton.IsEnabled = File.Exists(WpeExeBox.Text) && !processing && !detecting;
        DetectButton.IsEnabled = !processing && !detecting;
        CurrentWallpaperBox.IsEnabled = !detecting && CurrentWallpaperBox.Items.Count > 0;
        ApplyButton.IsEnabled = selected?.State == "completed" && selected.CanApply && !processing && File.Exists(WpeExeBox.Text) &&
            TargetBox.SelectedItem is TargetItem && (selected.ApplyManifest is null || selected.Restored);
        RollbackButton.IsEnabled = selected?.ApplyManifest is string application && File.Exists(application) && !processing && !selected.Restored;
        RollbackFileButton.IsEnabled = !processing;
        QueueEmpty.Visibility = jobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void LoadResultClicked(object sender, RoutedEventArgs e)
    {
        if (processing || tools is null) return;
        var dialog = new OpenFileDialog
        {
            Title = L("打开已有生成结果", "Open an existing result"),
            Filter = "Hybrid bake reports|bake.json|JSON|*.json", CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            JobItem job = LoadCompletedResult(dialog.FileName);
            JobItem? existing = jobs.FirstOrDefault(item => item.Request.OutputDirectory.Equals(job.Request.OutputDirectory, StringComparison.OrdinalIgnoreCase));
            if (existing is null) Enqueue(job); else QueueList.SelectedItem = existing;
            StatusText.Text = L("已载入生成结果。", "Existing result loaded.");
        }
        catch (Exception error) { StatusText.Text = error.Message; }
        RefreshControls();
    }

    private JobItem LoadCompletedResult(string reportPath)
    {
        if (tools is null) throw new InvalidOperationException("Generation tools are not available.");
        reportPath = Path.GetFullPath(reportPath);
        string output = Path.GetDirectoryName(reportPath)!;
        JsonObject report = JsonNode.Parse(File.ReadAllText(reportPath))?.AsObject()
            ?? throw new InvalidDataException("The saved result is not a JSON object.");
        string status = report["status"]?.GetValue<string>() ?? "";
        int schema = report["schema_version"]?.GetValue<int>() ?? 0;
        string? kind = report["artifact_kind"]?.GetValue<string>();
        HybridBakeRequest request;
        if (schema == 2 && kind is "hybrid_video_candidate" or "hybrid_video_probe")
        {
            if (status is not ("candidate_generated" or StaticOnlyBake.Status or "probe_generated" or "candidate_rejected_no_loop" or
                "candidate_rejected_composition" or "candidate_rejected_late_dependency" or "candidate_rejected_seam" or
                "candidate_rejected_hardware_decode" or "candidate_rejected_opaque_capture" or ResidualMasking.LayoutRejectedStatus or
                "candidate_rejected_capture_target" or CandidateScriptErrorGate.RejectedBakeStatus or EmbeddedVideoBudget.RejectedBakeStatus))
                throw new InvalidDataException("This hybrid report does not contain a finished result.");
            JsonObject savedPlan = report["plan"]?.DeepClone().AsObject()
                ?? throw new InvalidDataException("The hybrid result has no saved plan.");
            HybridPlanFormat.Validate(savedPlan);
            JsonObject settings = savedPlan["settings"]?.AsObject()
                ?? throw new InvalidDataException("The hybrid result has no generation settings.");
            request = new HybridBakeRequest(2, savedPlan, output,
                kind == "hybrid_video_probe" ? report["frames"]!.GetValue<ulong>() : 0, settings["device_uuid"]?.GetValue<string>(),
                // 重放一份已保存的结果时沿用它当时请求的播放版编码档位。
                PlaybackEncoder: report["playback_encoder"]?["requested"]?.GetValue<string>());
            if (string.IsNullOrWhiteSpace(request.Plan["source"]?.GetValue<string>()) ||
                !string.Equals(request.Plan["source_sha256"]?.GetValue<string>(), report["source_sha256"]?.GetValue<string>(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The saved source identity or generation request is invalid.");
        }
        else throw new InvalidDataException("Select a finished Hybrid bake.json result.");
        // Prefer the copied result folder; older work folders keep their project one level below.
        string? declaredProject = AppJsonPresentation.CandidateProjectPath(report);
        string? project = declaredProject is null ? null :
            new[] { output, Path.Combine(output, "project"), declaredProject }
                .FirstOrDefault(path => File.Exists(Path.Combine(path, "project.json")));
        if (declaredProject is not null && project is null)
            throw new FileNotFoundException("The generated project.json is missing from the result folder and recorded project path.", declaredProject);
        request = request with { ProjectDirectory = project };
        string gpuName = GpuBox.Items.OfType<VulkanDeviceInfo>()
            .FirstOrDefault(device => device.DeviceUuid.Equals(request.DeviceUuid, StringComparison.OrdinalIgnoreCase))?.Name
            ?? request.DeviceUuid ?? L("生成设备未记录", "Generation device not recorded");
        JsonObject definitions = AppJsonPresentation.LoadPropertyDefinitions(project ?? request.Plan["source"]!.GetValue<string>());
        bool canApply = project is not null && AppJsonPresentation.CandidateCanApply(report);
        return new JobItem(request, tools, gpuName, definitions)
        {
            State = "completed", ProjectPath = project, GenerationReportPath = reportPath, LatestReportPath = reportPath,
            CanApply = canApply,
            Detail = canApply
                ? L("已生成：可应用到桌面。", "Generated: can be applied to the desktop.")
                : L("已生成：循环记录版本不符，需重新生成后才能应用。",
                    "Generated: the loop record is from another version; regenerate before applying.")
        };
    }

    private async void GenerateClicked(object sender, RoutedEventArgs e)
    {
        RefreshControls();
        if (!GenerateButton.IsEnabled || tools is null) return;
        var gpu = GpuBox.SelectedItem as VulkanDeviceInfo;
        if (gpu is null) return;
        if (hybridPlan is not null)
        {
            string source = hybridPlan["source"]!.GetValue<string>();
            string output = AppEnvironment.NewWorkDirectory(source, OutputBox.Text.Trim());
            var hybrid = new HybridBakeRequest(2, hybridPlan.DeepClone().AsObject(), output, 0, gpu!.DeviceUuid,
                ProjectDirectory: AppEnvironment.NewOutput(OutputBox.Text.Trim(), source),
                PlaybackEncoder: SelectedPlaybackEncoder());
            Enqueue(new JobItem(hybrid, tools, gpu.Name, sourcePropertyDefinitions));
            queueRun = ProcessQueueAsync();
            await queueRun;
            return;
        }
    }

    private void Enqueue(JobItem job)
    {
        job.Translate(english);
        jobs.Add(job);
        QueueList.SelectedItem = job;
        RefreshControls();
    }

    private async Task ProcessQueueAsync()
    {
        if (processing) return;
        processing = true;
        try
        {
            while (!closeRequested && jobs.FirstOrDefault(j => j.State == "queued") is { } job)
            {
                activeJob = job;
                runCancellation?.Dispose();
                runCancellation = new CancellationTokenSource();
                job.State = "running";
                job.Translate(english);
                RunProgress.IsIndeterminate = true;
                RefreshControls();
                try
                {
                    var progress = MakeProgress(job);
                    var result = await Task.Run(() => new HybridBakeService(job.Tools).BakeAsync(job.Request, progress, runCancellation.Token));
                    job.ProjectPath = AppJsonPresentation.CandidateProjectPath(result);
                    job.LatestReportPath = job.GenerationReportPath;
                    string resultStatus = result["status"]?.GetValue<string>() ?? "candidate_generated";
                    job.CanApply = AppJsonPresentation.CandidateCanApply(result);
                    job.State = "completed";
                    bool effectPrefix = job.Request.Plan["route"]?.GetValue<string>() == "effect_prefix";
                    // 队列里只说人话，每条后面都指向报告文件；技术原文在那里一句没少。
                    job.Detail = resultStatus == StaticOnlyBake.Status
                        ? L("当前方案输出静态纹理并保留全部实时图层，无功耗收益；需使动态部分进入视频图层。",
                            "Current route yields a static texture plus all live layers, with no power saving; the animated part must move into the video layer.")
                        : resultStatus == "candidate_rejected_no_loop"
                        ? L("未检出循环周期，生成中止；原因见报告文件。",
                            "No loop period was detected; generation aborted. The report file states the reason.")
                        : resultStatus == "candidate_rejected_composition"
                        ? L("合成结果与原作不一致，生成中止；差异见自动检查结果。",
                            "The composition did not match the original; generation aborted. The automatic checks show the difference.")
                        : resultStatus == "candidate_rejected_late_dependency"
                        ? L("检出画面仍依赖实时图层，生成中止；依赖项见报告文件。",
                            "The picture was found to still depend on a live layer; generation aborted. The report file names the dependency.")
                        : resultStatus == "candidate_rejected_hardware_decode"
                        ? L("本机无法硬件解码生成的视频，生成中止；详情见报告文件。",
                            "This machine cannot hardware-decode the generated video; generation aborted. Details are in the report file.")
                        : resultStatus == CandidateScriptErrorGate.RejectedBakeStatus
                        ? L("成品脚本报错多于原作，生成中止；报错原文见报告文件。",
                            "The result raised more script errors than the original; generation aborted. The original errors are in the report file.")
                        : resultStatus == ResidualMasking.LayoutRejectedStatus
                        ? L("循环周期首尾不衔接，生成中止；原因见报告文件。",
                            "The loop period does not join end to start; generation aborted. The report file states the reason.")
                        : resultStatus is "candidate_rejected_capture_target" or EmbeddedVideoBudget.RejectedBakeStatus
                        ? L("生成中止：", "Generation aborted: ") +
                            (result["reason_localized"]?[english ? "en" : "zh"]?.GetValue<string>() ?? result["reason"]?.GetValue<string>() ?? "")
                        : resultStatus == "candidate_rejected_opaque_capture"
                        ? L("无法确认视频图层完全不透明，生成中止；详情见报告文件。",
                            "Opaque coverage by the video layer could not be confirmed; generation aborted. Details are in the report file.")
                        : job.CanApply ? L("已生成：可应用到桌面。", "Generated: can be applied to the desktop.")
                        : L("循环周期首尾不衔接，成品不可用；详情见报告文件。",
                            "The loop period does not join end to start, so this result is unusable. Details are in the report file.");
                    if (AppJsonPresentation.Number(result["source_script_error_count"]) > 0 ||
                        AppJsonPresentation.Number(result["full_capture_source_script_error_count"]) > 0)
                        job.Detail += " · " + AppJsonPresentation.SourceScriptErrorSummary(result, english, includeFullCapture: true);
                    if (StageTiming.Summary(result, english) is string stageSummary) job.Detail += " · " + stageSummary;
                }
                catch (OperationCanceledException)
                {
                    job.State = "cancelled";
                    job.Detail = L("已取消，中间文件保留。", "Cancelled; partial files are retained.");
                }
                catch (Exception error)
                {
                    job.State = "failed"; job.Detail = error.Message;
                    job.ErrorPath = await SaveErrorAsync(job, error);
                }
                finally
                {
                    job.Translate(english);
                    RunProgress.IsIndeterminate = false;
                    RunProgress.Value = job.State == "completed" ? 1 : 0;
                    activeJob = null;
                    StatusText.Text = job.StatusText + " · " + job.Title;
                    RefreshControls();
                }
            }
        }
        finally
        {
            processing = false; RefreshControls();
            if (closeRequested) Close();
        }
    }

    private IProgress<RenderProgress> MakeProgress(JobItem job) => new Progress<RenderProgress>(value =>
    {
        if (activeJob != job) return;
        RunProgress.IsIndeterminate = value.Fraction is null || value.Stage == "official_sampling";
        RunProgress.Value = value.Fraction is double amount ? Math.Clamp(amount, 0, 1) : 0;
        string message = value.Stage switch {
            "preflight" => L("正在校验文件与工具…", "Verifying files and tools…"),
            "baking" => L("正在预渲染…", "Prerendering…"),
            "validating" => L("正在校验输出画面…", "Verifying rendered output…"),
            "official_window_phase" => english ? value.Message
                : L("正在通过 Wallpaper Engine 对比原作与成品…", "Comparing the original and the result through Wallpaper Engine…"),
            "official_sampling" => L("正在实测 Wallpaper Engine 功耗…", "Measuring Wallpaper Engine power draw…"),
            "completed" => L("阶段完成…", "Stage complete…"),
            "rendering" => value.Message.EndsWith(" frames", StringComparison.Ordinal)
                ? value.Message.Replace(" frames", L(" 帧", " frames")) : L("正在渲染与编码…", "Rendering and encoding…"), _ => value.Message };
        job.Detail = message;
        StatusText.Text = job.Title + " · " + message;
    });

    private void CancelClicked(object sender, RoutedEventArgs e)
    {
        if (QueueList.SelectedItem is not JobItem job) return;
        if (job == activeJob) { runCancellation?.Cancel(); job.Detail = L("正在停止…", "Stopping…"); }
        else if (job.State == "queued") { job.State = "cancelled"; job.Translate(english); }
        RefreshControls();
    }

    private async void RetryClicked(object sender, RoutedEventArgs e)
    {
        if (QueueList.SelectedItem is not JobItem { State: "completed" or "cancelled" or "failed" } original) return;
        if (!AppEnvironment.OutputValid(OutputBox.Text.Trim(), original.Source))
        {
            StatusText.Text = L("输出目录无效：不能位于壁纸来源文件夹内。", "Invalid output directory: it must be outside the wallpaper source folder.");
            return;
        }
        var request = original.Request with { Plan = original.Request.Plan.DeepClone().AsObject(),
            OutputDirectory = AppEnvironment.NewWorkDirectory(original.Source, OutputBox.Text.Trim()),
            ProjectDirectory = AppEnvironment.NewOutput(OutputBox.Text.Trim(), original.Source) };
        Enqueue(original.Clone(request));
        await ProcessQueueAsync();
    }

    private void OpenResultClicked(object sender, RoutedEventArgs e)
    {
        if (QueueList.SelectedItem is JobItem job) OpenPath(job.ProjectPath ?? job.Request.OutputDirectory);
    }
    private void OpenErrorClicked(object sender, RoutedEventArgs e)
    {
        if (QueueList.SelectedItem is JobItem { ErrorPath: { } path }) OpenPath(path);
    }
    private void OpenReportClicked(object sender, RoutedEventArgs e)
    {
        if (QueueList.SelectedItem is JobItem { LatestReportPath: { } path }) OpenPath(path);
    }
    private void OpenPath(string path)
    {
        try { Process.Start(new ProcessStartInfo(Path.GetFullPath(path)) { UseShellExecute = true }); }
        catch (Exception error) { StatusText.Text = error.Message; }
    }

    // 图层清单：只展示分析结果与位置/显隐提示，不主动推荐排除；勾选才会在下一次分析里剔除。
    private void BuildLayerList()
    {
        if (!initialized) return;
        LayersEditor.Children.Clear();
        foreach (JsonObject layer in layerList?.OfType<JsonObject>() ?? [])
        {
            if (layer["id"] is not JsonValue identifier || !identifier.TryGetValue<int>(out int id)) continue;
            string name = layer["name"] is JsonValue value && value.TryGetValue<string>(out string? text) && text.Trim().Length > 0
                ? text.Trim() : L("未命名图层", "Unnamed layer");
            string kind = layer["kind"]?.GetValue<string>() switch {
                "image" => L("图像", "image"), "text" => L("文本", "text"), "particle" => L("粒子系统", "particle system"),
                "composite" => L("复合图层", "composite"), _ => L("其他", "other") };
            string allocation = layer["allocation"]?.GetValue<string>() switch {
                "video" => L("视频图层", "video layer"), "live" => L("实时图层", "live layer"),
                "excluded" => L("已排除", "excluded"), "omitted" => L("来源中已禁用", "disabled in the source"),
                _ => L("不渲染", "not rendered") };
            string quadrant = layer["quadrant"]?.GetValue<string>() switch {
                "top_left" => L("左上", "top left"), "top_right" => L("右上", "top right"),
                "bottom_left" => L("左下", "bottom left"), "bottom_right" => L("右下", "bottom right"),
                _ => L("居中", "centre") };
            string binding = layer["visible_binding"]?.GetValue<string>() switch {
                "script" => L("可见性由脚本控制", "visibility controlled by a script"), "animation" => L("可见性由动画控制", "visibility controlled by an animation"),
                "user_property" => L("可见性由壁纸属性控制", "visibility controlled by a wallpaper property"), _ => L("始终可见", "always visible") };
            double fraction = AppJsonPresentation.Number(layer["canvas_fraction"]) ?? 0;
            string hints = AppJsonPresentation.LayerHints(layer, english);
            var title = new TextBlock { Text = name, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.Normal,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x20, 0x2A, 0x38)) };
            var detail = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0),
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x74, 0x82, 0x99)),
                Text = $"{kind} · {allocation} · {quadrant} · {fraction:P1} · {binding}" +
                    (hints.Length == 0 ? "" : L(" · 备注：", " · notes: ") + hints) };
            var row = new StackPanel();
            row.Children.Add(title); row.Children.Add(detail);
            var box = new CheckBox { IsChecked = excludedLayerIds.Contains(id), Content = row, Padding = new Thickness(6, 0, 0, 0),
                Margin = new Thickness(0, 0, 8, 8) };
            box.Checked += (_, _) => ToggleLayerExclusion(id, true);
            box.Unchecked += (_, _) => ToggleLayerExclusion(id, false);
            LayersEditor.Children.Add(box);
        }
    }

    private void ToggleLayerExclusion(int id, bool excluded)
    {
        if (!(excluded ? excludedLayerIds.Add(id) : excludedLayerIds.Remove(id))) return;
        ++settingsRevision;
        analysisCancellation?.Cancel();
        if (hybridPlan is not null) { hybridPlan = null; UpdatePlanSummary(); }
        RefreshControls();
        StatusText.Text = L("图层排除项已变更。生成前需重新分析。", "Layer exclusions changed. Re-analysis required before generating.");
    }

    private void BuildPropertyEditors()
    {
        if (!initialized) return;
        PropertiesEditor.Children.Clear();
        var selected = QueueList.SelectedItem as JobItem;
        JsonObject definitions = selected?.PropertyDefinitions ?? sourcePropertyDefinitions;
        JsonObject? snapshot = selected?.FrozenProperties ?? hybridPlan?["snapshot_properties"] as JsonObject;
        JsonObject overrides = selected is null ? analysisPreviewOverrides : new();
        bool frozen = selected is not null;
        WallpaperEngineProperties.Resolution? wpe = frozen ? null : sourceWpeProperties;
        JsonObject currentValues = AppJsonPresentation.ComposePropertyValues(definitions, snapshot, overrides, wpe?.Values);
        IReadOnlySet<string> fromWpe = AppJsonPresentation.WpeMarkedKeys(frozen ? selected!.Request.Plan : hybridPlan, wpe, overrides);
        string sourceNote = AppJsonPresentation.PropertySourceNote(wpe, english);
        PropertyScopeText.Text = frozen
            ? L("这些是生成时使用的属性，已固定。修改需返回壁纸来源重新分析。",
                "These are the properties used at generation time and are now fixed. Changing them requires returning to the wallpaper source and re-analyzing.")
            : L("这些属性用于下一次生成。修改后需重新分析。",
                "These properties apply to the next generation. Changing them requires re-analysis.") + (sourceNote.Length == 0 ? "" : " " + sourceNote);
        AppPropertyDefinition[] ordered = AppJsonPresentation.OrderedDefinitions(definitions);
        PropertiesExpander.IsEnabled = ordered.Length > 0;
        static string Clean(string text) => WebUtility.HtmlDecode(Regex.Replace(text, "<[^>]*>", " ")).Trim();
        void Store(string key, JsonNode value)
        {
            if (frozen) return;
            overrides[key] = value.DeepClone();
            if (selected is null) InvalidatePropertyDependentAnalysis();
        }
        foreach (AppPropertyDefinition property in ordered)
        {
            string key = property.Key;
            JsonObject definition = property.Definition;
            string type = definition["type"]?.GetValue<string>() ?? "unknown";
            string label = Clean(definition["text"]?.GetValue<string>() ?? key);
            JsonNode? current = currentValues[key];
            var row = new StackPanel { Margin = new Thickness(0, 0, 8, 12) };
            if (type == "bool" && current is JsonValue boolValue && boolValue.TryGetValue<bool>(out bool active))
            {
                var box = new CheckBox { IsChecked = active, Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap } };
                box.Checked += (_, _) => Store(key, JsonValue.Create(true)!);
                box.Unchecked += (_, _) => Store(key, JsonValue.Create(false)!);
                row.Children.Add(box);
            }
            else
            {
                row.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 0, 0, 5) });
                if (type == "slider" && AppJsonPresentation.Number(current) is double value)
                {
                    double min = AppJsonPresentation.Number(definition["min"]) ?? 0;
                    double max = AppJsonPresentation.Number(definition["max"]) ?? 100;
                    if (!double.IsFinite(min) || !double.IsFinite(max) || min >= max || !double.IsFinite(value)) continue;
                    var dock = new DockPanel();
                    var display = new TextBlock { Text = value.ToString("0.###", CultureInfo.InvariantCulture), Width = 55, TextAlignment = TextAlignment.Right };
                    DockPanel.SetDock(display, Dock.Right); dock.Children.Add(display);
                    double step = AppJsonPresentation.Number(definition["step"]) ?? 1;
                    var slider = new Slider { Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max),
                        TickFrequency = double.IsFinite(step) && step > 0 ? step : 1, IsSnapToTickEnabled = true };
                    slider.ValueChanged += (_, args) => { Store(key, JsonValue.Create(args.NewValue)!); display.Text = args.NewValue.ToString("0.###", CultureInfo.InvariantCulture); };
                    dock.Children.Add(slider); row.Children.Add(dock);
                }
                else if (type == "combo" && definition["options"] is JsonArray options)
                {
                    var combo = new ComboBox { DisplayMemberPath = "Label" };
                    var items = options.OfType<JsonObject>().Where(o => o["value"] is not null)
                        .Select(o => new PropertyOption(Clean(o["label"]?.GetValue<string>() ?? o["value"]!.ToJsonString()), o["value"]!.DeepClone())).ToArray();
                    combo.ItemsSource = items;
                    combo.SelectedItem = items.FirstOrDefault(o => JsonNode.DeepEquals(o.Value, current));
                    combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is PropertyOption option) Store(key, option.Value); };
                    row.Children.Add(combo);
                }
                else if (type is "textinput" or "color" && current is JsonValue textValue &&
                    textValue.TryGetValue<string>(out string? text))
                {
                    var input = new TextBox { Text = text ?? "", FontSize = 12 };
                    input.LostFocus += (_, _) => Store(key, JsonValue.Create(input.Text)!);
                    row.Children.Add(input);
                }
                else row.Children.Add(new TextBlock { Text = L("只读 · ", "Read only · ") + type +
                    (current is null ? "" : " · " + current.ToJsonString()), FontSize = 11, TextWrapping = TextWrapping.Wrap, Opacity = .6 });
            }
            // 值来自用户在 Wallpaper Engine 里的设置（且没被面板改动覆盖）时标一句。
            if (fromWpe.Contains(key))
                row.Children.Add(new TextBlock { Text = L("取自 Wallpaper Engine 设置", "from Wallpaper Engine settings"), FontSize = 11, Opacity = .7,
                    Margin = new Thickness(0, 2, 0, 0) });
            PropertiesEditor.Children.Add(row);
        }
    }

    private void InvalidatePropertyDependentAnalysis()
    {
        if (suppressSettingsChanges) return;
        ++settingsRevision;
        analysisCancellation?.Cancel();
        if (hybridPlan is null) return;
        hybridPlan = null;
        UpdatePlanSummary(); RefreshControls();
        StatusText.Text = L("壁纸属性已变更。生成前需重新分析。", "Wallpaper properties changed. Re-analysis required before generating.");
    }

    private async void ValidateClicked(object sender, RoutedEventArgs e)
    {
        if (processing || QueueList.SelectedItem is not JobItem { State: "completed" } job) return;
        await RunJobOperationAsync(job, "reading_report", async token =>
        {
            JsonObject bake = JsonNode.Parse(await File.ReadAllTextAsync(job.GenerationReportPath, token))?.AsObject()
                ?? throw new InvalidDataException("Hybrid bake report is invalid.");
            job.LatestReportPath = job.GenerationReportPath;
            return AppJsonPresentation.HybridValidationSummary(bake, english);
        });
    }

    /// <summary>
    /// 烘完之后量一遍省了多少电：原来那张和做出来的各在官方 Wallpaper Engine 里播一次、各采样一次，
    /// 比出核显域的降幅，给一句人话，读数写进 gain.json 与 bake.json 的 measured_gain。
    /// </summary>
    private async void MeasurePlaybackClicked(object sender, RoutedEventArgs e)
    {
        if (processing || QueueList.SelectedItem is not JobItem { State: "completed" } job) return;
        string executable = WpeExeBox.Text.Trim();
        string original = Directory.Exists(job.Source) ? job.Source : Path.GetDirectoryName(job.Source) ?? job.Source;
        string? baked = job.ProjectPath;
        double fps = job.FpsNumerator / (double)job.FpsDenominator;
        await RunJobOperationAsync(job, "sampling", async token =>
        {
            if (baked is null || !Directory.Exists(baked)) throw new InvalidOperationException("The finished wallpaper folder is missing.");
            if (!File.Exists(executable)) throw new FileNotFoundException("Wallpaper Engine was not found at the chosen path.");
            string root = Path.Combine(job.Request.OutputDirectory, "gain-" + Guid.NewGuid().ToString("N")[..8]);
            string? presentMon = FindPresentMon();
            JsonObject before = await OfficialPerformanceSampler.SampleAsync(new(1, 0, "original", Path.Combine(root, "original"),
                Seconds: 45, TargetFps: fps, PresentMonPath: presentMon, SourceProject: original,
                WallpaperEngineExecutable: executable), MakeProgress(job), token);
            JsonObject after = await OfficialPerformanceSampler.SampleAsync(new(1, 0, "baked", Path.Combine(root, "baked"),
                Seconds: 45, TargetFps: fps, PresentMonPath: presentMon, SourceProject: baked,
                WallpaperEngineExecutable: executable), MakeProgress(job), token);
            JsonObject gain = SourcePowerVerdict.Gain(SourcePowerVerdict.FromSample(before), SourcePowerVerdict.FromSample(after));
            Directory.CreateDirectory(root);
            string gainPath = Path.Combine(root, "gain.json");
            await File.WriteAllTextAsync(gainPath, gain.ToJsonString(), token);
            job.LatestReportPath = gainPath;
            // bake.json 里的 measured_gain 一直是 not_verified 的占位，这里换成真读数。
            if (File.Exists(job.GenerationReportPath) &&
                JsonNode.Parse(await File.ReadAllTextAsync(job.GenerationReportPath, token)) is JsonObject bake)
            {
                bake["measured_gain"] = gain.DeepClone();
                await File.WriteAllTextAsync(job.GenerationReportPath, bake.ToJsonString(), token);
            }
            return SourcePowerVerdict.GainLine(gain, english ? Messages.English : Messages.Chinese);
        });
    }

    /// <summary>分析前在官方 Wallpaper Engine 里播一遍原来那张实测功耗；量不了就记下原因，绝不让分析失败。</summary>
    private async Task<JsonObject> MeasureSourcePowerAsync(string source, string output, double fps, CancellationToken token)
    {
        string executable = WpeExeBox.Text.Trim();
        StatusText.Text = L("正在实测原壁纸功耗（约 60 s）…", "Measuring original wallpaper power draw (about 60 s)…");
        try
        {
            if (!File.Exists(executable)) throw new FileNotFoundException("Wallpaper Engine was not found at the chosen path.");
            JsonObject sample = await OfficialPerformanceSampler.SampleAsync(new(1, 0, "source power", output,
                Seconds: 30, TargetFps: fps, PresentMonPath: FindPresentMon(),
                SourceProject: Directory.Exists(source) ? source : Path.GetDirectoryName(source) ?? source,
                WallpaperEngineExecutable: executable), null, token);
            return SourcePowerVerdict.FromSample(sample);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return SourcePowerVerdict.Skipped(error.Message);
        }
    }

    private static string? FindPresentMon()
    {
        string[] names = ["PresentMon.exe", "PresentMon-2.5.1-x64.exe"];
        foreach (string name in names)
        {
            string bundled = Path.Combine(AppContext.BaseDirectory, "performance", name);
            if (File.Exists(bundled)) return bundled;
        }
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        for (int depth = 0; directory is not null && depth < 8; ++depth, directory = directory.Parent)
            foreach (string name in names)
            {
                string local = Path.Combine(directory.FullName, ".tools", "presentmon", name);
                if (File.Exists(local)) return local;
            }
        return null;
    }

    private async void ExportClicked(object sender, RoutedEventArgs e)
    {
        if (QueueList.SelectedItem is not JobItem { State: "completed", ProjectPath: { } project } job) return;
        if (job.ExportArchive is string archive) { OpenPath(archive); return; }
        if (processing) return;
        var dialog = new OpenFolderDialog { Title = L("选择 ZIP 保存位置", "Choose ZIP destination") };
        if (dialog.ShowDialog(this) != true) return;
        string directory = AppEnvironment.NewOutput(dialog.FolderName, job.Source);
        await RunJobOperationAsync(job, "exporting", async token =>
        {
            var result = await Task.Run(() => CandidateExporter.ExportAsync(new(1, project, directory, CreateZip: true), token));
            job.ExportArchive = result["archive"]!.GetValue<string>();
            job.LatestReportPath = Path.Combine(directory, "export.json");
            return L("ZIP 已导出。", "ZIP export complete.");
        });
    }

    private void ChooseWallpaperExecutable(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Wallpaper Engine|wallpaper64.exe;wallpaper32.exe|Executable|*.exe", CheckFileExists = true };
        if (File.Exists(WpeExeBox.Text)) dialog.InitialDirectory = Path.GetDirectoryName(WpeExeBox.Text);
        if (dialog.ShowDialog(this) == true) UpdateInstallationDefaults(dialog.FileName);
    }
    private void WallpaperExecutableChanged(object sender, TextChangedEventArgs e)
    {
        if (!initialized) return;
        TargetBox.ItemsSource = null; TargetBox.SelectedIndex = -1;
        CurrentWallpaperBox.ItemsSource = null; CurrentWallpaperBox.SelectedIndex = -1;
        RefreshControls();
    }
    private void UpdateInstallationDefaults(string executable)
    {
        updatingInstallation = true;
        try
        {
            WpeExeBox.Text = executable;
            if (!File.Exists(executable)) return;
            string assets = Path.Combine(Path.GetDirectoryName(executable)!, "assets");
            if (AppEnvironment.AssetsValid(assets)) AssetsBox.Text = assets;
            string output = NativeEnvironment.FindWallpaperProjectDirectory(executable);
            if (output.Length > 0 && (OutputBox.Text.Length == 0 ||
                string.Equals(OutputBox.Text, defaultOutputDirectory, StringComparison.OrdinalIgnoreCase)))
            {
                defaultOutputDirectory = output;
                OutputBox.Text = output;
            }
        }
        finally { updatingInstallation = false; }
    }
    private async void DetectClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            UpdateInstallationDefaults(AppEnvironment.FindWallpaperExecutable());
            if (File.Exists(WpeExeBox.Text)) await LoadTargetsAsync(autoImport: true);
            else StatusText.Text = L("未找到 Wallpaper Engine。手动指定程序路径与壁纸。", "Wallpaper Engine not found. Specify the executable path and a wallpaper manually.");
        }
        catch (Exception error) { StatusText.Text = L("检测失败：", "Detection failed: ") + error.Message; }
    }
    private void CurrentWallpaperChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized || CurrentWallpaperBox.SelectedItem is not CurrentWallpaperItem current) return;
        if (current.Source is null) StatusText.Text = current.Detail;
        else ImportSource(current.Source);
    }
    private async void RefreshTargetsClicked(object sender, RoutedEventArgs e) => await LoadTargetsAsync();
    private async Task LoadTargetsAsync(bool autoImport = false)
    {
        if (detecting) return;
        detecting = true;
        CurrentWallpaperBox.ItemsSource = null;
        CurrentWallpaperBox.SelectedIndex = -1;
        RefreshControls();
        string executable = WpeExeBox.Text.Trim();
        try
        {
            var controller = new WallpaperController(executable);
            var targets = await controller.ReadTargetsAsync();
            if (WpeExeBox.Text.Trim() != executable) return;
            TargetBox.ItemsSource = targets.Select(t => new TargetItem(t)).ToArray();
            TargetBox.SelectedIndex = -1;
            var activeTargets = await controller.ReadCurrentTargetsAsync();
            var current = new List<CurrentWallpaperItem>();
            foreach (var target in activeTargets)
            {
                try
                {
                    var observed = await controller.ObserveAsync(target.Location);
                    string source = await Task.Run(() => AppEnvironment.ValidateSource(observed.File));
                    string title = Path.GetFileName(Directory.Exists(source) ? source : Path.GetDirectoryName(source)) ?? source;
                    string projectFile = Path.Combine(Directory.Exists(source) ? source : Path.GetDirectoryName(source)!, "project.json");
                    if (File.Exists(projectFile))
                        title = JsonNode.Parse(await File.ReadAllTextAsync(projectFile))?["title"]?.GetValue<string>() ?? title;
                    string evidence = observed.Evidence == "official_getWallpaper"
                        ? L("当前壁纸", "Current wallpaper") : L("当前屏幕的已保存设置", "Saved setting for the current screen");
                    current.Add(new(target.Location + " · " + title, source, evidence + ": " + source));
                }
                catch (Exception error)
                {
                    current.Add(new(target.Location + L(" · 无法导入", " · Cannot import"), null,
                        error.Message));
                }
            }
            if (WpeExeBox.Text.Trim() != executable) return;
            ShowCurrentWallpapers(current.ToArray(), autoImport);
        }
        catch (Exception error) { StatusText.Text = L("读取屏幕失败：", "Failed to read screens: ") + error.Message; }
        finally { detecting = false; RefreshControls(); }
    }

    private void ShowCurrentWallpapers(CurrentWallpaperItem[] current, bool autoImport)
    {
        CurrentWallpaperBox.ItemsSource = current;
        CurrentWallpaperBox.SelectedIndex = -1;
        StatusText.Text = current.Length == 0
            ? L("未检出正在播放的壁纸。手动选择或拖入壁纸来源。", "No running wallpaper detected. Select or drop a wallpaper source manually.")
            : L("已检出正在播放的壁纸。在上方选择屏幕。", "Running wallpapers detected. Select a screen above.");
        if (autoImport && SourceBox.Text.Length == 0 && current.Length == 1 && current[0].Source is not null)
            CurrentWallpaperBox.SelectedIndex = 0;
    }

    private async void ApplyClicked(object sender, RoutedEventArgs e)
    {
        RefreshControls();
        if (!ApplyButton.IsEnabled || QueueList.SelectedItem is not JobItem { ProjectPath: { } project } job ||
            TargetBox.SelectedItem is not TargetItem target) return;
        string executable = WpeExeBox.Text.Trim();
        string directory = Path.Combine(job.Request.OutputDirectory, "apply-" + Guid.NewGuid().ToString("N")[..8]);
        job.ApplyManifest = Path.Combine(directory, "apply.json");
        job.Restored = false;
        await RunJobOperationAsync(job, "applying", async token =>
        {
            var result = await new WallpaperController(executable).ApplyAsync(new(1, project, target.Target.Profile, target.Target.Location, directory), token);
            job.LatestReportPath = job.ApplyManifest;
            return result["status"]?.GetValue<string>() == "applied"
                ? L("已应用到该屏幕。", "Applied to that screen.")
                : L("切换指令已发送至 Wallpaper Engine。", "Switch request sent to Wallpaper Engine.");
        });
        if (!File.Exists(job.ApplyManifest)) job.ApplyManifest = null;
        RefreshControls();
    }

    private async void RollbackClicked(object sender, RoutedEventArgs e)
    {
        if (processing) return;
        JobItem? job = sender == RollbackFileButton ? null : QueueList.SelectedItem as JobItem;
        string? manifest = job?.ApplyManifest;
        if (manifest is null)
        {
            var dialog = new OpenFileDialog
            {
                Title = L("选择壁纸应用记录", "Choose a wallpaper application record"),
                Filter = "Wallpaper application record|apply.json|JSON|*.json", CheckFileExists = true
            };
            if (dialog.ShowDialog(this) != true) return;
            manifest = dialog.FileName;
            job = jobs.FirstOrDefault(item => string.Equals(item.ApplyManifest, manifest, StringComparison.OrdinalIgnoreCase));
        }
        await RunJobOperationAsync(job, "restoring", async token =>
        {
            JsonObject saved = JsonNode.Parse(await File.ReadAllTextAsync(manifest, token))?.AsObject()
                ?? throw new InvalidDataException("The wallpaper application record is invalid.");
            string executable = saved["executable"]?.GetValue<string>()
                ?? throw new InvalidDataException("The application record has no Wallpaper Engine executable.");
            if (!File.Exists(executable)) throw new FileNotFoundException("The recorded Wallpaper Engine installation is missing.", executable);
            var result = await new WallpaperController(executable).RollbackAsync(manifest, token);
            if (job is not null) { job.Restored = true; job.LatestReportPath = manifest; }
            return result["status"]?.GetValue<string>() == "restored"
                ? L("已回滚到先前壁纸。", "Rolled back to the previous wallpaper.")
                : L("回滚指令已发送。", "Rollback request sent.");
        });
    }

    private async Task RunJobOperationAsync(JobItem? job, string state, Func<CancellationToken, Task<string>> operation)
    {
        if (processing) return;
        processing = true; activeJob = job;
        runCancellation?.Dispose(); runCancellation = new CancellationTokenSource();
        if (job is not null) { job.State = state; job.Translate(english); job.Detail = ""; }
        RunProgress.IsIndeterminate = true; RefreshControls();
        try
        {
            StatusText.Text = await operation(runCancellation.Token);
            if (job is not null) job.Detail = StatusText.Text;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = L("已取消：已生成文件保留，桌面状态见应用记录。", "Cancelled: the generated files are retained; the application record shows the desktop state.");
            if (job is not null) job.Detail = StatusText.Text;
        }
        catch (Exception error)
        {
            StatusText.Text = error.Message;
            if (job is not null) { job.Detail = StatusText.Text; job.ErrorPath = await SaveErrorAsync(job, error); }
        }
        finally
        {
            if (job is not null) { job.State = "completed"; job.Translate(english); }
            activeJob = null; processing = false;
            RunProgress.IsIndeterminate = false; RunProgress.Value = 0; RefreshControls();
            if (closeRequested) Close(); else await ProcessQueueAsync();
        }
    }

    private async void OfficialPreviewClicked(object sender, RoutedEventArgs e)
    {
        if (QueueList.SelectedItem is not JobItem { State: "completed", ProjectPath: { } project } job || processing) return;
        string executable = WpeExeBox.Text.Trim();
        await RunJobOperationAsync(job, "previewing", async token =>
        {
            JsonObject settings = job.Request.Plan["settings"]!.AsObject();
            var (width, height) = (settings["width"]!.GetValue<uint>(), settings["height"]!.GetValue<uint>());
            if (officialPreviewExecutable is not null)
                await new WallpaperController(officialPreviewExecutable).CloseWindowAsync(officialPreviewName, token);
            await new WallpaperController(executable).OpenInWindowAsync(project, officialPreviewName, width, height, token, activate: true);
            officialPreviewExecutable = executable;
            return L("已在独立窗口播放成品。关闭该窗口即结束预览。",
                "The result is playing in a separate window. Closing that window ends the preview.");
        });
    }

    private static async Task<string?> SaveErrorAsync(JobItem job, Exception error)
    {
        try
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WpeBaker/Logs");
            Directory.CreateDirectory(folder);
            string file = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".txt");
            await File.WriteAllTextAsync(file, $"Source: {job.Source}\nOutput: {job.Request.OutputDirectory}\n\n{error}");
            return file;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private void WindowClosing(object? sender, CancelEventArgs e)
    {
        analysisCancellation?.Cancel();
        if (!processing) return;
        closeRequested = true; e.Cancel = true;
        foreach (var job in jobs.Where(j => j.State == "queued")) { job.State = "cancelled"; job.Translate(english); }
        runCancellation?.Cancel();
        StatusText.Text = L("正在结束运行中的任务，完成后关闭窗口…", "Finishing the running job before the window closes…");
    }

    internal void PrepareLayoutQa(string source, string assets, string? completedBake = null, string? analyzedPlan = null)
    {
        SourceBox.Text = source; AssetsBox.Text = assets;
        GpuBox.ItemsSource = VulkanDevices.Enumerate();
        GpuBox.SelectedIndex = 0;
        if (File.Exists(WpeExeBox.Text))
        {
            string executable = WpeExeBox.Text;
            var targets = Task.Run(() => new WallpaperController(executable).ReadTargetsAsync()).GetAwaiter().GetResult();
            TargetBox.ItemsSource = targets.Select(t => new TargetItem(t)).ToArray(); TargetBox.SelectedIndex = -1;
        }
        if (completedBake is not null)
        {
            var job = LoadCompletedResult(completedBake);
            if (job.SourceSha256 != JsonNode.Parse(File.ReadAllText(completedBake))?["source_sha256"]?.GetValue<string>()) throw new InvalidDataException("Layout QA source and bake do not match.");
            Enqueue(job);
            DesktopExpander.IsExpanded = true;
        }
        // 截图要看的是分析完之后的样子：把 analyze 写出来的 plan 原样装进界面，和点完按钮得到的是同一份。
        if (analyzedPlan is not null)
        {
            JsonObject plan = JsonNode.Parse(File.ReadAllText(analyzedPlan))?.AsObject()
                ?? throw new InvalidDataException("Layout QA plan is not a JSON object.");
            if (!string.Equals(plan["source"]?.GetValue<string>(), source, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Layout QA plan belongs to a different wallpaper source.");
            hybridPlan = plan;
            audioEffectsChoiceKnown = AppJsonPresentation.HasAudioEffectsChoice(plan);
            turnOffKindsChosen = false;
            UpdatePlanSummary();
            BuildPropertyEditors();
        }
        RefreshControls();
    }


    private static class StateBrushes
    {
        internal static readonly System.Windows.Media.Brush Ok = Freeze("#157347");
        internal static readonly System.Windows.Media.Brush Bad = Freeze("#C0392B");
        internal static readonly System.Windows.Media.Brush Busy = Freeze("#3465D9");
        internal static readonly System.Windows.Media.Brush Muted = Freeze("#5A6A80");
        private static System.Windows.Media.Brush Freeze(string hex)
        {
            var brush = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }
    }

    private abstract class ObservableItem : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    }
    private sealed record TargetItem(WallpaperTarget Target)
    {
        public string Label => $"{Target.Location} · {Target.Profile}";
    }
    private sealed record CurrentWallpaperItem(string Label, string? Source, string Detail);
    private sealed record PropertyOption(string Label, JsonNode Value);
    private sealed class JobItem(HybridBakeRequest request, NativeTools tools, string gpuName, JsonObject? definitions = null) : ObservableItem
    {
        private string detail = "";
        public HybridBakeRequest Request { get; } = request;
        public NativeTools Tools { get; } = tools;
        public string GpuName { get; private set; } = gpuName;
        public string Source => Request.Plan["source"]!.GetValue<string>();
        public string SourceSha256 => Request.Plan["source_sha256"]!.GetValue<string>();
        public string Assets => Request.Plan["assets"]!.GetValue<string>();
        public uint FpsNumerator => Request.Plan["settings"]!["fps_numerator"]!.GetValue<uint>();
        public uint FpsDenominator => Request.Plan["settings"]!["fps_denominator"]!.GetValue<uint>();
        public string? DeviceUuid => Request.DeviceUuid ?? Request.Plan["settings"]?["device_uuid"]?.GetValue<string>();
        public string Title => Path.GetFileName(Directory.Exists(Source) ? Source.TrimEnd(Path.DirectorySeparatorChar) : Path.GetDirectoryName(Source)) ?? "Wallpaper";
        public string Settings => $"{FpsNumerator}/{FpsDenominator} fps · {GpuName}";
        public string State { get; set; } = "queued";
        public string StatusText { get; private set; } = "";
        // 队列里用颜色区分状态：完成绿、失败/取消红、进行中蓝、等待灰。
        public System.Windows.Media.Brush StatusBrush => State switch
        {
            "completed" => CanApply ? StateBrushes.Ok : StateBrushes.Bad,
            "failed" or "cancelled" => StateBrushes.Bad,
            "queued" => StateBrushes.Muted,
            _ => StateBrushes.Busy,
        };
        public string Detail { get => detail; set { detail = value; Changed(); } }
        public string? ProjectPath { get; set; }
        public string? ErrorPath { get; set; }
        public string? ExportArchive { get; set; }
        public JsonObject PropertyDefinitions { get; } = (JsonObject?)definitions?.DeepClone() ?? new();
        public JsonObject? FrozenProperties => Request.Plan["snapshot_properties"]?.AsObject();
        public string GenerationReportPath { get; set; } = Path.Combine(request.OutputDirectory, "bake.json");
        public string? LatestReportPath { get; set; }
        public string? ApplyManifest { get; set; }
        public bool Restored { get; set; }
        public bool CanApply { get; set; } = true;
        public JobItem Clone(HybridBakeRequest replacement) => new(replacement, Tools, GpuName, PropertyDefinitions);
        public void Translate(bool english)
        {
            if (GpuName is "生成设备未记录" or "Generation device not recorded")
                GpuName = english ? "Generation device not recorded" : "生成设备未记录";
            foreach (string prefix in new[] { "已载入：", "Loaded: " })
                if (Detail.StartsWith(prefix, StringComparison.Ordinal)) { Detail = (english ? "Loaded: " : "已载入：") + Detail[prefix.Length..]; break; }
            StatusText = State switch { "queued" => english ? "Queued" : "等待中", "running" => english ? "Generating" : "正在生成",
                "completed" => english ? "Completed" : "已完成", "cancelled" => english ? "Cancelled" : "已取消",
                "failed" => english ? "Failed" : "失败", "previewing" => english ? "Making previews" : "正在生成预览",
                "applying" => english ? "Applying wallpaper" : "正在应用壁纸", "restoring" => english ? "Restoring wallpaper" : "正在恢复壁纸",
                "sampling" => english ? "Measuring current playback" : "正在测量当前播放",
                "reading_report" => english ? "Reading validation report" : "正在读取验证报告",
                "exporting" => english ? "Exporting ZIP" : "正在导出 ZIP", _ => State };
            (string Zh, string En)[] details = [
                ("已生成：可应用到桌面。", "Generated: can be applied to the desktop."),
                ("特效前缀候选已生成，可应用。", "Effect-prefix candidate created and can be applied."),
                ("已取消，中间文件保留。", "Cancelled; partial files are retained."),
                ("ZIP 已导出。", "ZIP export complete.") ];
            foreach (var pair in details) if (Detail == pair.Zh || Detail == pair.En) { Detail = english ? pair.En : pair.Zh; break; }
            Changed(nameof(Settings));
            Changed(nameof(StatusText));
            Changed(nameof(StatusBrush));
        }
    }
}
