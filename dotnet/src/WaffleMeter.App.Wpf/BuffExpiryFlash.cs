using System.Windows.Media;
using System.Windows.Threading;

namespace WaffleMeter.App.Wpf;

/// <summary>
/// 곧 끝나는 버프 아이콘 위에서 껐다 켜지는 어두운 마스크 — "버프 종료 3초 전 알림"의 시각 신호다.
///
/// <para><b>왜 아이콘을 흐리는 게 아니라 위에 덮는가.</b> <c>Opacity</c> 채널은 이미 "쿨타임 중 아이콘 회색"이
/// 쓰고 있어서(<see cref="BuffSlotVM.IconOpacity"/>) 같은 틱에 서로를 덮어쓴다. 또 <c>Opacity &lt; 1</c> 인
/// 서브트리는 알파 합성용 중간 표면을 만드는데, 이 저장소는 게이지 실측에서 그 표면이 비용의 정체였음을
/// 이미 확인했다. 형제로 얹은 불투명 마스크는 그 둘을 모두 피한다.</para>
///
/// <para><b>왜 Storyboard 가 아닌가.</b> 앱이 <c>RenderOptions.ProcessRenderMode = SoftwareOnly</c> 로 돌고
/// 오버레이는 <c>AllowsTransparency</c> 레이어드 창이라 GPU 합성 경로가 없다. Storyboard 는 클럭이 30~60fps 라
/// 이 오버레이의 기본 2fps 대비 15~30배를 그리게 된다. 그래서 <see cref="TierSheen"/> 와 같은 모양을 쓴다 —
/// 행 바깥의 공유 <b>비동결</b> 브러시 하나를 저빈도 타이머가 두드리고, 슬롯은 그걸 함께 가리킨다. 프로퍼티
/// 한 번 쓰기로 화면의 모든 임박 슬롯이 같은 위상으로 깜빡인다.</para>
///
/// <para>임박한 슬롯이 없으면 타이머를 멈추므로, 평소 유휴 비용은 정확히 0이다.</para>
/// </summary>
public static class BuffExpiryFlash
{
    /// <summary>토글 간격. 220ms = 약 2.3Hz 로, 3초 창에 13~14번 깜빡인다 — 게임 UI 의 임박 점멸과 같은 체감
    /// 속도다. 더 빠르면 소프트웨어 렌더에서 비용만 늘고 눈에는 떨림으로 읽힌다.</summary>
    private const int IntervalMs = 220;

    /// <summary>마스크가 켜졌을 때의 검정 알파. 아이콘이 "꺼진 듯" 어두워지되 무엇인지는 계속 읽혀야 한다.</summary>
    private static readonly Color On = Color.FromArgb(0xAE, 0x00, 0x00, 0x00);
    private static readonly Color Off = Colors.Transparent;

    private static readonly DispatcherTimer Timer = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromMilliseconds(IntervalMs),
    };

    /// <summary>공유 비동결 브러시. 슬롯들이 이걸 그대로 가리키므로 색 한 번 바꾸면 전부 다시 그려진다.</summary>
    private static readonly SolidColorBrush MaskBrush = new(Off);

    private static bool _lit;

    static BuffExpiryFlash() => Timer.Tick += (_, _) =>
    {
        _lit = !_lit;
        MaskBrush.Color = _lit ? On : Off;
    };

    /// <summary>슬롯의 마스크가 칠할 브러시.</summary>
    public static Brush Mask => MaskBrush;

    /// <summary>
    /// 이번 틱에 임박한(=점멸해야 할) 슬롯이 몇 개인지 알린다. 0 이면 타이머를 멈추고 마스크를 투명으로
    /// 되돌린다 — 마지막 깜빡임이 <b>켜진 채</b> 굳어 아이콘이 영영 어두워지는 것을 막는다. 오버레이를 숨기거나
    /// 파킹할 때도 0 으로 불러야 한다.
    /// </summary>
    public static void SetDemand(int count)
    {
        if (count > 0)
        {
            if (!Timer.IsEnabled)
            {
                _lit = true;                 // 첫 프레임부터 눈에 띄게 시작한다
                MaskBrush.Color = On;
                Timer.Start();
            }

            return;
        }

        if (Timer.IsEnabled)
        {
            Timer.Stop();
        }

        _lit = false;
        MaskBrush.Color = Off;
    }
}
