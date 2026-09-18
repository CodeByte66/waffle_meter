using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using WaffleMeter.App.Core;
using WaffleMeter.Data;

namespace WaffleMeter.App.Wpf;

/// <summary>
/// 쿨타임 오버레이의 "표시할 스킬 선택" 픽커. 직업별로 일반/스티그마 두 묶음의 토글 칩을 낸다.
/// <para>행 클래스(<see cref="SkillJobGroupViewModel"/>)는 참가요청 배지 픽커와 공유하고, 카탈로그와 저장
/// 키만 다르다: 저쪽은 컴파일된 167코드 목록과 <c>joinSkills.hidden</c>, 이쪽은 배포 자산에서 읽은 221코드와
/// <c>cooldownUi.hidden</c> 이다. ⚠️ 두 픽커가 같은 <see cref="ISkillVisibility"/> 인스턴스를 쓰면 배지 토글이
/// 쿨타임 표시를 함께 바꾼다 — 집합을 참조로 넘기기 때문이다.</para>
/// <para>창은 공유하지 않는다. 오버레이가 그리는 것은 <b>내 직업 하나</b>뿐인데 목록은 9개 직업 221개라,
/// 기본을 "내 직업만"으로 두지 않으면 매번 자기 직업을 찾아 스크롤해야 한다.</para>
/// </summary>
public sealed class CooldownPickerViewModel : INotifyPropertyChanged
{
    private readonly List<SkillJobGroupViewModel> _all;

    public CooldownPickerViewModel(CooldownCatalog catalog, CooldownVisibility visibility)
    {
        Dictionary<int, string> jobName = SkillCatalog.JobPrefix.ToDictionary(kv => kv.Value, kv => kv.Key);
        var names = new Dictionary<int, string>();

        _all = catalog.Skills
            .GroupBy(s => s.Job)
            .OrderBy(g => g.Key)
            .Where(g => jobName.ContainsKey(g.Key))
            .Select(g =>
            {
                foreach (CooldownSkillInfo s in g)
                {
                    names[s.BaseCode] = s.Name;
                }

                // 픽커 안의 순서는 오버레이의 슬롯 순서(Order)와 같게 둔다 — 두 화면에서 같은 스킬을 다른
                // 자리에서 찾게 만들 이유가 없다.
                var grouped = new GroupedJobSkills(
                    jobName[g.Key],
                    g.Where(s => !s.IsStigma).OrderBy(s => s.Order).Select(s => s.BaseCode).ToList(),
                    g.Where(s => s.IsStigma).OrderBy(s => s.Order).Select(s => s.BaseCode).ToList(),
                    // 쿨타임 카탈로그에는 패시브가 없다 — 쿨이 도는 스킬만 싣는다. 빈 묶음은 픽커에서 접힌다.
                    Array.Empty<int>());

                return new SkillJobGroupViewModel(
                    grouped, visibility, OnChipToggled,
                    code => names.TryGetValue(code, out string? n) ? n : code.ToString());
            })
            .ToList();

        _jobBand = _all.ToDictionary(g => g, g => SkillCatalog.JobPrefix[g.Job]);
        Groups = new ObservableCollection<SkillJobGroupViewModel>(_all);
        RebuildGroups();
    }

    private readonly Dictionary<SkillJobGroupViewModel, int> _jobBand;

    /// <summary>화면에 실제로 그려지는 묶음. "내 직업만" 이 켜져 있고 직업을 알면 하나로 줄어든다.</summary>
    public ObservableCollection<SkillJobGroupViewModel> Groups { get; }

    /// <summary>모든 직업의 묶음 — 전체 선택/해제는 화면에 보이는 것에만 적용해야 하므로 원본을 따로 둔다.</summary>
    public IReadOnlyList<SkillJobGroupViewModel> AllGroups => _all;

    private int _ownJobBand;
    /// <summary>미터가 인식한 캐릭터의 직업 대역(11~19). 0 이면 아직 모른다. 창을 열 때 App 이 채운다 —
    /// 생성 시점에는 대개 아직 모르기 때문이다.</summary>
    public int OwnJobBand
    {
        get => _ownJobBand;
        set
        {
            if (_ownJobBand == value)
            {
                return;
            }

            _ownJobBand = value;
            OnPropertyChanged(nameof(OwnJobBand));
            OnPropertyChanged(nameof(CanFilterByJob));
            OnPropertyChanged(nameof(OwnJobName));
            OnPropertyChanged(nameof(FilterLabel));
            RebuildGroups();
        }
    }

    /// <summary>직업을 알아야 "내 직업만" 을 걸 수 있다.</summary>
    public bool CanFilterByJob => _ownJobBand != 0;

    public string OwnJobName =>
        _all.FirstOrDefault(g => _jobBand[g] == _ownJobBand)?.Job ?? string.Empty;

    /// <summary>토글 라벨. 직업을 알면 그 이름을 넣어 무엇이 걸리는지 분명히 한다.</summary>
    public string FilterLabel => CanFilterByJob ? $"{OwnJobName}만 보기" : "내 직업만 보기";

    private bool _onlyOwnJob = true;
    /// <summary>기본 켜짐. 오버레이는 내 직업 스킬만 그리므로, 다른 직업 200개를 함께 스크롤하게 두는 것은
    /// 목록을 찾기 어렵게만 만든다. 끄면 9개 직업이 전부 나온다.</summary>
    public bool OnlyOwnJob
    {
        get => _onlyOwnJob;
        set
        {
            if (_onlyOwnJob == value)
            {
                return;
            }

            _onlyOwnJob = value;
            OnPropertyChanged(nameof(OnlyOwnJob));
            RebuildGroups();
        }
    }

    /// <summary>헤더의 "23 / 23 켜짐". <b>지금 보이는 묶음 기준</b>이라 필터와 뜻이 어긋나지 않는다 —
    /// "내 직업만"이 켜져 있는데 221 을 띄우면 화면에 없는 숫자를 읽게 된다.</summary>
    public string SummaryText
    {
        get
        {
            int on = Groups.Sum(g => g.SelectedCount);
            int total = Groups.Sum(g => g.TotalCount);
            return $"{on} / {total} 켜짐";
        }
    }

    /// <summary>지금 보이는 묶음 전체를 켠다/끈다. 안 보이는 직업까지 건드리면 되돌릴 방법이 없다.</summary>
    public void SelectAllVisible()
    {
        foreach (SkillJobGroupViewModel g in Groups.ToList())
        {
            g.SelectAll();
        }
    }

    public void DeselectAllVisible()
    {
        foreach (SkillJobGroupViewModel g in Groups.ToList())
        {
            g.DeselectAll();
        }
    }

    /// <summary>토글이 일어난 뒤. App 이 오버레이를 즉시 다시 그리도록 쓴다.</summary>
    public event Action? Changed;

    /// <summary>공유 집합을 바깥에서 갈아끼웠을 때(설정 가져오기) 칩을 다시 읽는다. 알림만 하므로
    /// <see cref="Changed"/> 로 되돌아오지 않는다.</summary>
    public void Refresh()
    {
        foreach (SkillJobGroupViewModel group in _all)
        {
            group.Refresh();
        }

        OnPropertyChanged(nameof(SummaryText));
    }

    private void OnChipToggled()
    {
        OnPropertyChanged(nameof(SummaryText));
        Changed?.Invoke();
    }

    private void RebuildGroups()
    {
        List<SkillJobGroupViewModel> want = _onlyOwnJob && _ownJobBand != 0
            ? _all.Where(g => _jobBand[g] == _ownJobBand).ToList()
            : _all;

        // 목록이 실제로 달라질 때만 갈아끼운다 — 매번 Clear/Add 하면 스크롤 위치가 튄다.
        if (want.Count == Groups.Count && want.SequenceEqual(Groups))
        {
            return;
        }

        Groups.Clear();
        foreach (SkillJobGroupViewModel g in want)
        {
            Groups.Add(g);
        }

        OnPropertyChanged(nameof(SummaryText));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
