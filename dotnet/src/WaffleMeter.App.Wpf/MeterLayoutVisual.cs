using System.Collections.Concurrent;
using System.Windows;
using WaffleMeter.App.Core;

namespace WaffleMeter.App.Wpf;

/// <summary>
/// <see cref="MeterLayout"/> 의 순수 수치를 XAML 이 바로 물 수 있는 WPF 타입으로 감싼 어댑터.
///
/// <para>⚠️ <b>Brush·Color 를 절대 싣지 않는다.</b> 색은 전부 <c>DynamicResource Skin.*</c> 로 남아야
/// 스킨 4종 교체를 따라간다 — 레이아웃이 색을 들고 있으면 스킨을 바꿔도 안 따라오는 스냅샷이 된다
/// (<c>ReplayWindow</c> 가 <c>TryFindResource</c> 1회성 스냅샷으로 같은 함정을 밟은 전례가 있다).</para>
///
/// <para>(id, rowHeight) 로 캐시한다. 값이 행마다·틱마다 다시 만들어지면 <c>Thickness</c> 박싱이
/// 초당 수십 번 쌓인다.</para>
/// </summary>
public sealed class MeterLayoutVisual
{
    private static readonly ConcurrentDictionary<(string Id, int RowHeight), MeterLayoutVisual> Cache = new();

    private MeterLayoutVisual(MeterLayout spec, int rowHeight)
    {
        Spec = spec;
        RowHeight = rowHeight;

        CardPadding = new Thickness(spec.CardPaddingH, spec.CardPaddingV, spec.CardPaddingH, spec.CardPaddingV);
        CardMargin = new Thickness(0, spec.CardMarginV, 0, spec.CardMarginV);

        // 무대는 하단 1px 헤어라인만 — 위·좌·우가 없어야 행이 '틈 없는 판'으로 이어진다.
        CardBorder = spec.HasCardChrome
            ? (spec.CardBorderV >= 2.0 ? new Thickness(1) : new Thickness(0, 0, 0, spec.CardBorderV))
            : default;

        CardRadius = new CornerRadius(spec.CardRadius);
        GaugeRadius = spec.GaugeRadius;
        GaugeExclusionLeft = MeterLayout.GaugeExclusionLeft(spec.Id, rowHeight);
        FxBandHeight = MeterLayout.FxBandHeight(spec.Id, rowHeight);
        RowMinHeight = rowHeight;
        RankChipVisibility = spec.ShowRankChip ? Visibility.Visible : Visibility.Collapsed;

        // 전장만 보스칸이 고정 높이다(20px 독립 게이지 띠 + 두 줄 텍스트). 나머지는 현행대로 행 높이에 연동.
        BossHeight = spec.BossStyle == BossStyle.Band ? 84.0 : rowHeight + 6.0;
        BossBandVisibility = spec.BossStyle == BossStyle.Band ? Visibility.Visible : Visibility.Collapsed;
        BossCanvasVisibility = spec.BossStyle == BossStyle.Canvas ? Visibility.Visible : Visibility.Collapsed;
        BossReadoutVisibility = spec.BossStyle == BossStyle.Readout ? Visibility.Visible : Visibility.Collapsed;
    }

    public MeterLayout Spec { get; }

    public int RowHeight { get; }

    public Thickness CardPadding { get; }

    public Thickness CardMargin { get; }

    public Thickness CardBorder { get; }

    public CornerRadius CardRadius { get; }

    public double GaugeRadius { get; }

    /// <summary>장식을 비워 둘 왼쪽 폭. 옛 리터럴 72 를 대신하는 계산값이다.</summary>
    public double GaugeExclusionLeft { get; }

    /// <summary>장식이 실제로 그려지는 세로 대역(28/28/27). 테스트가 이 값을 잠근다.</summary>
    public double FxBandHeight { get; }

    /// <summary>
    /// 행 카드 Border 의 MinHeight. ⚠️이게 <c>GaugeFxLayer</c> 의 **유일한 높이 공급원**이다 —
    /// 카드 크롬을 0 으로 만들더라도 Border 자체와 이 값은 반드시 남아야 장식이 살아 있다.
    /// </summary>
    public double RowMinHeight { get; }

    public Visibility RankChipVisibility { get; }

    public bool HasCardChrome => Spec.HasCardChrome;

    /// <summary>레이아웃이 '직업색 게이지가 곧 행'을 전제하는가 — 게이지 형태 콤보를 잠글지 판단한다.</summary>
    public bool RequiresFillGauge => Spec.RequiresFillGauge;

    public double BossHeight { get; }

    public Visibility BossBandVisibility { get; }

    public Visibility BossCanvasVisibility { get; }

    public Visibility BossReadoutVisibility { get; }

    public static MeterLayoutVisual For(string? id, int rowHeight)
    {
        MeterLayout spec = MeterLayout.For(id);
        return Cache.GetOrAdd((spec.Id, rowHeight), key => new MeterLayoutVisual(MeterLayout.For(key.Id), key.RowHeight));
    }

    /// <summary>현행과 수치가 같은 기본값. 바인딩이 아직 안 붙은 순간에도 화면이 깨지지 않게 한다.</summary>
    public static readonly MeterLayoutVisual Default = For(MeterLayout.DefaultId, 36);
}
