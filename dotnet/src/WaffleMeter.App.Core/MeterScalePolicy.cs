namespace WaffleMeter.App.Core;

/// <summary>
/// 미터 전체 크기(배율) 규칙. WPF 타입이 없으므로 헤드리스 테스트가 전부 덮는다.
///
/// <para><b>불변식 하나:</b> <c>Window.Width / 배율</c> — 이하 <b>논리 열 예산</b>(baseWidth) — 은 배율이
/// 바뀌어도 보존된다. 종전에는 배율이 루트 <c>LayoutTransform</c> 안에 있고 <c>Window.Width</c> 는 그
/// <b>바깥</b>에 있어서, 세로축엔 <c>SizeToContent="Height"</c> 라는 짝이 있는데 <b>가로축엔 짝이 없었다</b>:
/// 배율을 130% 로 올리면 내부 논리 폭이 490÷1.3 ≈ 377 DIP 로 오히려 <b>줄어</b> 이름·[서버]태그·전투력
/// 배지가 예외도 로그도 없이 잘렸다. 폭을 배율에 묶으면 그 결함군이 통째로 닫힌다.</para>
///
/// <para><b>새 설정 키는 없다.</b> baseWidth 는 <c>meterWidth ÷ meterScalePercent</c> 로 유도되므로 기존
/// 사용자는 두 값이 그대로고, 업데이트 후 첫 실행이 픽셀 단위로 종전과 같다 — 마이그레이션 0.</para>
///
/// <para>🔴 <b>높이를 받는 오버로드를 만들지 마라.</b> 높이는 배율이 만들어 낸 값이라 거기서 배율을
/// 되읽으면 (배율→콘텐츠 높이→창 높이→배율) 되먹임이 즉시 닫힌다. 배율은 <b>폭에서만</b> 유도한다.</para>
/// </summary>
public static class MeterScalePolicy
{
    /// <summary>배율 하한·상한·기본(퍼센트).
    /// <para>⚠️ 범위를 넓히지 마라. <c>TierPalette</c>·<c>NameFxPalette</c>·<c>TierSheen</c> 세 파일이
    /// "소프트웨어 렌더에서 75~130%" 를 전제로 값을 골라 놨다 — 넓히면 그 셋의 설계 근거가 동시에
    /// 낡는다.</para></summary>
    public const int ScaleMin = 75, ScaleMax = 130, ScaleDefault = 100;

    /// <summary>논리 열 예산의 바닥(DIP). 배율 100% 일 때의 <c>Window.MinWidth</c> 와 같은 값이고,
    /// 실제 하한은 배율을 곱해서 만든다 — <see cref="MinWindowWidth"/>.</summary>
    public const double BaseWidthMin = 320.0;

    /// <summary>100% 로 되돌아오는 디텐트 폭(퍼센트포인트). 연속 배율에서 "보통"이 한 제스처로 잡히지
    /// 않으면 사용자는 영영 99% 나 101% 에 머문다.</summary>
    public const int DetentTolerance = 2;

    /// <summary>드래그한 핸들이 뜻하는 것.</summary>
    public enum Gesture
    {
        /// <summary>미터 전체 크기(배율). 좌/우 가장자리 전용.</summary>
        Scale,

        /// <summary>종전 그대로 — 폭/높이를 직접 바꾸고, 높이가 실제로 변했으면 자동 높이를 고정한다.</summary>
        Free,
    }

    /// <summary>
    /// 이 히트코드가 배율 제스처인가. <b>좌/우 가장자리만</b> 참이다.
    /// <para>🔑 이 배정이 <see cref="WindowResizePolicy"/> 를 한 줄도 건드리지 않게 해 주는 지점이다:
    /// <see cref="WindowResizePolicy.IsManualAfterDrag"/> 는 <c>HtLeft</c>/<c>HtRight</c> 에 대해
    /// <b>무조건 false</b> 를 돌려주므로(세로 핸들이 아니다), 배율 제스처는 높이 고정 래치를
    /// <b>구조적으로</b> 켤 수 없다. 그 래치는 한 번 켜지면 드래그로는 안 풀리는 sticky 라 겹치면 위험하다.</para>
    /// <para>⚠️ 모서리를 배율로 가져가고 싶어질 것이다. 가져가지 마라 — 모서리는 사용자가 폭과 높이를
    /// <b>직접</b> 맞추는 유일한 수단이고, 그걸 배율로 덮으면 기능이 사라진다. 기능을 없애 규칙을
    /// 지키려 한 것이 v2.8.1 이 저지른 실수의 형태다.</para>
    /// </summary>
    public static Gesture MeaningOf(int hitCode) =>
        hitCode is WindowResizePolicy.HtLeft or WindowResizePolicy.HtRight ? Gesture.Scale : Gesture.Free;

    /// <summary>배율을 유효 범위로 자른다. 남의 공유코드는 <c>SettingsBundleApplier</c> 가 값 검증 없이
    /// 그대로 심으므로 읽는 쪽에서도 잘라야 한다.</summary>
    public static int ClampScale(int percent) => Math.Clamp(percent, ScaleMin, ScaleMax);

    /// <summary>100% 근처면 100% 로 붙인다.</summary>
    public static int Detent(int percent) => Math.Abs(percent - ScaleDefault) <= DetentTolerance ? ScaleDefault : percent;

    /// <summary>이 배율에서의 <c>Window.MinWidth</c>. 배율을 따라가지 않으면 130% 에서 320 까지 좁혀져
    /// 오른쪽이 조용히 잘리고, 75% 에서는 240 이면 될 것이 320 에 걸려 빈 띠가 남는다.</summary>
    public static double MinWindowWidth(int percent) => BaseWidthMin * ClampScale(percent) / 100.0;

    /// <summary>창 폭 → 논리 열 예산.</summary>
    public static double BaseWidth(double windowWidth, int percent) =>
        windowWidth * 100.0 / ClampScale(percent);

    /// <summary>논리 열 예산 → 창 폭. 하한은 <see cref="MinWindowWidth"/> 와 같은 바닥을 쓴다.</summary>
    public static double WindowWidth(double baseWidth, int percent)
    {
        int pct = ClampScale(percent);
        return Math.Max(BaseWidthMin, baseWidth) * pct / 100.0;
    }

    /// <summary>
    /// 드래그 중인 폭에서 배율을 읽는다 — 제스처 시작 시점의 (폭, 배율) 대비 비율이다.
    /// <para>시작 폭을 기준으로 삼는 이유: 상수 490 을 기준으로 잡으면 폭을 한 번 조절해 둔 사용자가
    /// 가장자리를 잡는 순간 배율이 튄다. 또 <c>meterWidthTierChipMigrated</c>(420→490 run-once)와도
    /// 충돌하지 않는다.</para>
    /// </summary>
    public static int ScaleFromDrag(double startWidth, int startPercent, double currentWidth)
    {
        if (startWidth <= 0 || currentWidth <= 0)
        {
            return ClampScale(startPercent);
        }

        int raw = (int)Math.Round(ClampScale(startPercent) * currentWidth / startWidth, MidpointRounding.AwayFromZero);
        return Detent(ClampScale(raw));
    }
}
