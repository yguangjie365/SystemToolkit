using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SystemToolkit.Core.Music.Models;
using SystemToolkit.Core.Music.Online;
using SystemToolkit.Core.Music.Services;
using SystemToolkit.UI.Common;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SystemToolkit.Modules.MusicManager;

/// <summary>
/// 音乐管理 VM 播放器分部（OM-6）：全屏播放器三风格 + 封面装载/主色提取 + 音质菜单。
/// </summary>
/// <remarks>
/// <para><b>三风格 = 同一覆盖层的三种主视觉模板</b>（透明彩胶居中圆盘 / 沉浸封面出血中央歌词 /
/// 现代左卡右列），共享底部控制条与播放状态——不复制整套 UI，View 用
/// <see cref="IsVinylStyle"/> 等可见性触发器切模板。</para>
/// <para><b>主色管线</b>：起播/切歌 → <see cref="LoadCoverAsync"/> 取封面（本地
/// <c>IMusicTagReader.ReadCover</c>；在线经代理 URL 下载）→ 解码 BitmapSource
/// （<see cref="CurrentCoverImage"/>，三模板共用）→ <see cref="PaletteMath"/> 提取
/// <see cref="CurrentAccentBrush"/>（彩胶 auto / 沉浸 scrim 派生）。封面缺失走中性回退。</para>
/// </remarks>
public partial class MusicManagerViewModel
{
    // ════════ 播放器风格 ════════

    /// <summary>全屏播放器风格。</summary>
    /// <summary>全屏播放器风格（枚举名避开生成属性 <c>PlayerStyle</c> 的同名冲突）。</summary>
    public enum PlayerStyleKind
    {
        /// <summary>透明彩胶：深色衬底 + 居中旋转唱片（auto/custom 主色）。默认风格。</summary>
        Vinyl,

        /// <summary>沉浸：封面全屏出血 + 中央歌词随曲滚动 + 底部半透明控制条。</summary>
        Immersion,

        /// <summary>现代：白净表面 + 左侧大封面卡片 + 右侧信息与操作列。</summary>
        Modern,
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVinylStyle))]
    [NotifyPropertyChangedFor(nameof(IsImmersionStyle))]
    [NotifyPropertyChangedFor(nameof(IsModernStyle))]
    private PlayerStyleKind _playerStyle = PlayerStyleKind.Vinyl;

    /// <summary>当前是否为彩胶风格（XAML 模板可见性）。</summary>
    public bool IsVinylStyle => PlayerStyle == PlayerStyleKind.Vinyl;

    /// <summary>当前是否为沉浸风格。</summary>
    public bool IsImmersionStyle => PlayerStyle == PlayerStyleKind.Immersion;

    /// <summary>当前是否为现代风格。</summary>
    public bool IsModernStyle => PlayerStyle == PlayerStyleKind.Modern;

    /// <summary>风格名（设置持久化/无障碍用）。</summary>
    public string PlayerStyleText => PlayerStyle switch
    {
        PlayerStyleKind.Immersion => "沉浸",
        PlayerStyleKind.Modern => "现代",
        _ => "透明彩胶",
    };

    /// <summary>顶栏切换播放器风格（参数 Vinyl/Immersion/Modern）。</summary>
    [RelayCommand]
    private void SwitchPlayerStyle(string? style)
    {
        PlayerStyle = style switch
        {
            "Immersion" => PlayerStyleKind.Immersion,
            "Modern" => PlayerStyleKind.Modern,
            _ => PlayerStyleKind.Vinyl,
        };
        RefreshPlayerChromeBrushes();
    }

    // ════════ 封面 + 主色 ════════

    private BitmapSource? _currentCoverImage;

    /// <summary>当前曲封面（本地内嵌解码 / 在线经代理下载）；无封面为 null。</summary>
    public BitmapSource? CurrentCoverImage
    {
        get => _currentCoverImage;
        private set
        {
            if (SetProperty(ref _currentCoverImage, value))
            {
                OnPropertyChanged(nameof(HasCover));
            }
        }
    }

    /// <summary>当前是否有可用封面（三模板据此回退）。</summary>
    public bool HasCover => CurrentCoverImage is not null;

    private SolidColorBrush _coverAccentBrush = CoverColorFactory.Neutral;

    /// <summary>封面主色（PaletteMath 提取 + 钳制，冻结刷）；无封面时中性深灰。</summary>
    public SolidColorBrush CurrentAccentBrush
    {
        get => _coverAccentBrush;
        private set => SetProperty(ref _coverAccentBrush, value);
    }

    private long _coverSeq;

    private SolidColorBrush _vinylBackgroundBrush = CoverColorFactory.LightTint(CoverColorFactory.Neutral);

    /// <summary>彩胶浅背景（主色高亮低饱和近白染色；对照 NexBox 透明彩胶）。</summary>
    public SolidColorBrush VinylBackgroundBrush
    {
        get => _vinylBackgroundBrush;
        private set => SetProperty(ref _vinylBackgroundBrush, value);
    }

    private SolidColorBrush _immersionBackgroundBrush = CoverColorFactory.DarkImmersive(CoverColorFactory.Neutral);

    /// <summary>沉浸深背景（主色压暗；对照 NexBox 沉浸）。</summary>
    public SolidColorBrush ImmersionBackgroundBrush
    {
        get => _immersionBackgroundBrush;
        private set => SetProperty(ref _immersionBackgroundBrush, value);
    }

    private SolidColorBrush _modernBackgroundBrush = CoverColorFactory.MidTone(CoverColorFactory.Neutral);

    /// <summary>现代中调背景（主色中亮度中饱和；对照 NexBox 现代）。</summary>
    public SolidColorBrush ModernBackgroundBrush
    {
        get => _modernBackgroundBrush;
        private set => SetProperty(ref _modernBackgroundBrush, value);
    }

    private Brush _playerForegroundBrush = ThemeBrush.Find("Brush_TextPrimary", "#1F1F1F");

    /// <summary>控制条/顶栏主前景（沉浸深底用 OnDark 浅字，其余浅底用主题深字）。</summary>
    public Brush PlayerForegroundBrush
    {
        get => _playerForegroundBrush;
        private set => SetProperty(ref _playerForegroundBrush, value);
    }

    private Brush _playerMutedBrush = ThemeBrush.Find("Brush_TextMuted", "#6B7280");

    /// <summary>控制条/顶栏次要前景（随风格深浅切换）。</summary>
    public Brush PlayerMutedBrush
    {
        get => _playerMutedBrush;
        private set => SetProperty(ref _playerMutedBrush, value);
    }

    private void RefreshPlayerChromeBrushes()
    {
        bool dark = PlayerStyle == PlayerStyleKind.Immersion;
        PlayerForegroundBrush = dark
            ? ThemeBrush.Find("Brush_OnDark", "#F5F5F4")
            : ThemeBrush.Find("Brush_TextPrimary", "#1F1F1F");
        PlayerMutedBrush = dark
            ? ThemeBrush.Find("Brush_OnDarkMuted", "#A8A29E")
            : ThemeBrush.Find("Brush_TextMuted", "#6B7280");
    }

    /// <summary>起播/切歌后装载封面与主色（后台 IO；结果经 RunOnUi 回 UI）。</summary>
    private async Task LoadCoverAsync(MusicSong song)
    {
        long seq = ++_coverSeq; // 竞态：快速切歌时旧封面 IO 不得覆盖新曲
        BitmapSource? image = null;
        try
        {
            if (song.IsOnline && !string.IsNullOrWhiteSpace(song.Online?.Cover))
            {
                image = await LoadOnlineCoverAsync(song.Online!.Cover);
            }
            else if (song.HasEmbeddedCover && _tagReader is not null)
            {
                MusicCover? cover = await Task.Run(() => _tagReader.ReadCover(song.LocalPath));
                if (cover is { Data.Length: > 0 })
                {
                    image = Decode(cover.Data);
                }
            }
        }
        catch (Exception ex)
        {
            // 封面加载失败不影响播放，但可见（🔴 不静默）
            _log.Warn($"[Music] 封面加载失败：{song.Name}（{ex.Message}）");
        }

        if (seq != _coverSeq)
        {
            return;
        }

        RunOnUi(() =>
        {
            CurrentCoverImage = image;
            SolidColorBrush accent = image is null ? CoverColorFactory.Neutral : CoverColorFactory.FromBitmap(image);
            CurrentAccentBrush = accent;
            VinylBackgroundBrush = CoverColorFactory.LightTint(accent);
            ImmersionBackgroundBrush = CoverColorFactory.DarkImmersive(accent);
            ModernBackgroundBrush = CoverColorFactory.MidTone(accent);
        });
    }

    /// <summary>在线封面：代理 URL（防盗链）→ 下载字节 → 解码。代理不可用则跳过（有本地图源的不受影响）。</summary>
    private async Task<BitmapSource?> LoadOnlineCoverAsync(string rawUrl)
    {
        if (_audioProxy is null)
        {
            return null;
        }

        string proxy = await _audioProxy.GetProxiedCoverUrlAsync(rawUrl);
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        byte[] bytes = await client.GetByteArrayAsync(proxy);
        return Decode(bytes);
    }

    private static BitmapSource? Decode(byte[] bytes)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(bytes);
        image.EndInit();
        image.Freeze();
        return image;
    }

    // ════════ 音质菜单 ════════

    /// <summary>音质档（值 = 平台 level 参数；标签按 NexBox 音质菜单中文）。</summary>
    public sealed record QualityOption(string Key, string Label);

    /// <summary>五级音质（网易 jymaster→standard；QQ 当前固定 standard，选择仍记录偏好）。</summary>
    public IReadOnlyList<QualityOption> QualityOptions { get; } =
    [
        new("jymaster", "母带"),
        new("hires", "Hi-Res"),
        new("lossless", "无损"),
        new("exhigh", "高品质"),
        new("standard", "标准"),
    ];

    /// <summary>当前音质展示名（底部角标与菜单选中态）。</summary>
    public string SelectedQualityText => QualityOptions.FirstOrDefault(o => o.Key == PreferredQuality)?.Label ?? "高品质";

    /// <summary>选择音质（参数 = level key）。当前曲为在线且正在播放时立即按新音质重取 URL。</summary>
    [RelayCommand]
    private async Task SetQualityAsync(string? key)
    {
        if (string.IsNullOrEmpty(key) || !QualityOptions.Any(o => o.Key == key))
        {
            return;
        }

        PreferredQuality = key;
        OnPropertyChanged(nameof(SelectedQualityText));

        // 🔴 AsyncRelayCommand 吞异常——重取由 PlayCurrentCoreAsync 内部兜住；
        // 仅当当前曲为在线曲且播放引擎在位时重取（本地曲只记录偏好）
        if (QueueCurrent?.IsOnline == true && ResolveEngine() is { } engine)
        {
            ScanStatusText = $"已切换音质：{SelectedQualityText}，正在重新获取…";
            await PlayCurrentCoreAsync(engine);
        }
    }

    // ════════ 播放器内 EQ（OM-7） ════════

    /// <summary>EQ 用户配置（开关/preamp/10 段增益；钳制在模型内完成）。</summary>
    public EqProfile Eq { get; } = EqProfile.Flat();

    private bool _isEqAvailable;

    /// <summary>当前引擎是否支持 EQ（可选能力接口探测；不支持时 EQ UI 禁用并提示）。</summary>
    public bool IsEqAvailable
    {
        get => _isEqAvailable;
        private set => SetProperty(ref _isEqAvailable, value);
    }

    /// <summary>EQ 滑条行（10 段；Gain 双向绑滑条，变更即热更引擎）。</summary>
    public sealed class EqBandVm : ObservableObject
    {
        public int Index { get; }
        public int Frequency { get; }

        private readonly MusicManagerViewModel _owner;

        public EqBandVm(MusicManagerViewModel owner, int index)
        {
            _owner = owner;
            Index = index;
            Frequency = EqProfile.Frequencies[index];
        }

        /// <summary>频段标签（Hz/kHz 自适应）。</summary>
        public string Label => Frequency >= 1000 ? $"{Frequency / 1000.0:0.#}k" : $"{Frequency}";

        private double _gain;

        /// <summary>当前段增益（dB）。</summary>
        public double Gain
        {
            get => _gain;
            set
            {
                double clamped = Math.Clamp(value, -EqProfile.MaxGainDb, EqProfile.MaxGainDb);
                if (SetProperty(ref _gain, clamped))
                {
                    _owner.Eq[Index] = clamped;
                    _owner.ApplyEqToEngine();
                }
            }
        }

        /// <summary>重置显示值（预设应用/重置后同步滑条）。</summary>
        internal void RefreshFrom(double gain) => SetProperty(ref _gain, gain);
    }

    private readonly ObservableCollection<EqBandVm> _eqBands = [];

    /// <summary>10 段滑条行（构造期初始化；滑条双向绑 Gain，变更即热更引擎）。</summary>
    public IReadOnlyList<EqBandVm> EqBands => _eqBands;

    private void InitEqBands()
    {
        if (_eqBands.Count > 0)
        {
            return;
        }

        foreach (int i in Enumerable.Range(0, EqProfile.BandCount))
        {
            _eqBands.Add(new EqBandVm(this, i));
        }
    }

    /// <summary>把当前 <see cref="Eq"/> 推给引擎（引擎不支持时静默——UI 已按 IsEqAvailable 禁用）。</summary>
    private void ApplyEqToEngine()
    {
        if (ResolveEngine() is IEqualizerEngine eqEngine)
        {
            try
            {
                eqEngine.ApplyEqualizer(Eq);
            }
            catch (Exception ex)
            {
                // 🔴 AsyncRelayCommand 吞异常已知坑的同步版——就地显式化
                ScanStatusText = $"EQ 应用失败：{ex.Message}";
                _log.Warn($"[Music] EQ 应用失败：{ex.Message}");
            }
        }
    }

    /// <summary>开关 EQ（开/关切换由引擎重建音频图，保持播放位置）。</summary>
    [RelayCommand]
    private void ToggleEq()
    {
        Eq.Enabled = !Eq.Enabled;
        ApplyEqToEngine();
        ScanStatusText = Eq.Enabled ? "均衡器已开启" : "均衡器已关闭";
    }

    /// <summary>应用内置预设（参数 = 预设名；未知名忽略）。</summary>
    [RelayCommand]
    private void ApplyEqPreset(string? name)
    {
        EqProfile.EqPreset? preset = EqProfile.FindPreset(name ?? string.Empty);
        if (preset is null)
        {
            return;
        }

        Eq.Enabled = true;
        Eq.PreampDb = preset.Preamp;
        for (int i = 0; i < EqProfile.BandCount; i++)
        {
            Eq[i] = preset.Gains[i];
            EqBands[i].RefreshFrom(Eq[i]); // 同步滑条显示
        }

        ApplyEqToEngine();
        ScanStatusText = $"已应用预设：{preset.Name}";
    }

    /// <summary>全部归零（保持开启）。</summary>
    [RelayCommand]
    private void ResetEq()
    {
        Eq.PreampDb = 0;
        for (int i = 0; i < EqProfile.BandCount; i++)
        {
            Eq[i] = 0;
            EqBands[i].RefreshFrom(0);
        }

        ApplyEqToEngine();
        ScanStatusText = "均衡器已归零";
    }
}
