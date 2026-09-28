using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
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
    private readonly CooldownCatalog _catalog;
    private readonly MeterSettings? _settings;

    /// <param name="settings">배치 순서(<c>cooldownUi.order</c>)를 읽고 쓰는 곳. 없으면 배치 영역은 안내문만
    /// 띄우고 아무것도 저장하지 않는다(설정 없이 창만 그려 보는 도구용).</param>
    public CooldownPickerViewModel(CooldownCatalog catalog, CooldownVisibility visibility, MeterSettings? settings = null)
    {
        _catalog = catalog;
        _settings = settings;
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
            UpdateArrangeHint();
        }
    }

    // ---- 배치(순서) ----------------------------------------------------------------------------------------
    //
    // 미리보기는 오버레이가 <b>지금 실제로 그리는 목록</b>을 그대로 받는다(App 이 틱마다 밀어 넣는다). 표시 여부
    // 필터와 "안 배운 스킬은 안 깐다"까지 이미 적용된 목록이라, 여기서 보는 칸과 게임 화면의 칸이 늘 같다.
    // 🔑 보이는 것이 곧 실제 순서여야 한다 — 버프 픽커의 ▲▼ 는 목록이 그 순서로 재정렬되지 않아 "눌러도 반응
    // 없는 버튼"으로 읽혀 제거됐다(SettingsWindow.xaml 의 위치 고정 주석).

    /// <summary>미리보기 한 칸의 피치(아이콘 34 + 좌우 여백 2+2). XAML 의 칸 크기·여백과 같아야 한다 —
    /// 다르면 미리보기가 오버레이와 다른 자리에서 줄을 바꾼다.</summary>
    public const double ArrangePitch = 38;

    /// <summary>오버레이가 그리는 순서 그대로의 칸들. 드래그 중에는 이 컬렉션 자체가 움직인다.</summary>
    public ObservableCollection<CooldownArrangeItem> ArrangeItems { get; } = new();

    private double _arrangeMaxWidth = 8 * ArrangePitch;
    /// <summary>미리보기의 폭 상한 = "한 줄 최대 개수" × 피치. 오버레이와 같은 자리에서 줄이 바뀌게 한다.</summary>
    public double ArrangeMaxWidth { get => _arrangeMaxWidth; private set => Set(ref _arrangeMaxWidth, value); }

    private string _arrangeHint = string.Empty;
    /// <summary>미리보기가 비어 있을 때의 안내. 칸이 있으면 빈 문자열이고 화면에서 접힌다.</summary>
    public string ArrangeHint { get => _arrangeHint; private set => Set(ref _arrangeHint, value); }

    /// <summary>"기본 순서로" 를 누를 수 있는가 — 직업을 알고 저장할 곳이 있어야 한다.</summary>
    public bool CanResetArrangement => _settings is not null && _ownJobBand != 0;

    private bool _dragging;
    private List<int> _orderBeforeDrag = [];

    /// <summary>오버레이가 그리는 행을 받아 미리보기를 맞춘다. 드래그 중에는 무시한다 — 틱마다 덮으면 사용자가
    /// 끌고 있는 칸이 제자리로 튕긴다. 칸 구성이 같으면 컬렉션을 건드리지 않는다(스크롤·호버가 튀지 않게).</summary>
    public void SetArrangeSource(IReadOnlyList<SkillCooldownView> drawnRows, int perRow)
    {
        ArrangeMaxWidth = Math.Clamp(perRow, 4, 16) * ArrangePitch;
        if (_dragging)
        {
            return;
        }

        List<SkillCooldownView> mine = _ownJobBand == 0
            ? []
            : drawnRows.Where(r => r.Job == _ownJobBand).ToList();

        if (mine.Count != ArrangeItems.Count || !mine.Select(r => r.GroupId).SequenceEqual(ArrangeItems.Select(i => i.Code)))
        {
            ArrangeItems.Clear();
            foreach (SkillCooldownView r in mine)
            {
                ArrangeItems.Add(new CooldownArrangeItem(r.GroupId, r.Name, JoinIcons.Skill(r.DisplayCode)));
            }
        }

        UpdateArrangeHint();
    }

    public void BeginArrangeDrag(CooldownArrangeItem item)
    {
        _dragging = true;
        _orderBeforeDrag = ArrangeItems.Select(i => i.Code).ToList();
        item.IsDragged = true;
    }

    /// <summary>드래그 중 칸을 옮긴다. 저장은 놓을 때 한 번만 한다(<see cref="EndArrangeDrag"/>) — 지나가는 칸마다
    /// 쓰면 프리셋 자동 저장과 오버레이 다시 그리기가 칸 수만큼 돈다.</summary>
    public void MoveArrangeItem(int from, int to)
    {
        if (from == to || from < 0 || to < 0 || from >= ArrangeItems.Count || to >= ArrangeItems.Count)
        {
            return;
        }

        ArrangeItems.Move(from, to);
    }

    /// <summary>드래그를 끝내고 지금 미리보기 순서를 저장한다. 숨긴·안 배운 스킬은 미리보기에 없으므로 제자리를
    /// 지킨다(<see cref="SkillCooldownOrder.Rearrange"/>).</summary>
    public void EndArrangeDrag()
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        foreach (CooldownArrangeItem item in ArrangeItems)
        {
            item.IsDragged = false;
        }

        // 제자리에 놓았으면 저장하지 않는다. 지금 순서를 그대로 "사용자 배치"로 굳히면 그 직업은 기본 순서를
        // 잃는다 — 이후 패치로 생긴 일반 스킬이 일반 묶음이 아니라 스티그마 뒤에 붙는다.
        if (_settings is null || _ownJobBand == 0 || ArrangeItems.Select(i => i.Code).SequenceEqual(_orderBeforeDrag))
        {
            return;
        }

        List<int> stored = SkillCooldownOrder.Parse(_settings.CooldownUiOrder);
        List<int> jobOrder = SkillCooldownOrder.JobOrder(_catalog.Skills.Where(s => s.Job == _ownJobBand), stored);
        List<int> next = SkillCooldownOrder.Rearrange(stored, _ownJobBand, jobOrder, ArrangeItems.Select(i => i.Code).ToList());
        _settings.CooldownUiOrder = SkillCooldownOrder.Format(next);
        Changed?.Invoke(); // 게임 화면의 오버레이도 바로 따라오게
    }

    /// <summary>내 직업의 배치를 지운다. 다른 직업의 배치는 그대로다.</summary>
    public void ResetArrangement()
    {
        if (!CanResetArrangement)
        {
            return;
        }

        _settings!.CooldownUiOrder =
            SkillCooldownOrder.Format(SkillCooldownOrder.WithoutJob(SkillCooldownOrder.Parse(_settings.CooldownUiOrder), _ownJobBand));
        Changed?.Invoke();
    }

    private void UpdateArrangeHint()
    {
        ArrangeHint = _ownJobBand == 0 ? "캐릭터가 인식되면 오버레이에 그려지는 순서대로 여기에 나타납니다."
            : ArrangeItems.Count == 0 ? "오버레이에 표시할 스킬이 없습니다. 아래에서 스킬을 켜 주세요."
            : string.Empty;
        OnPropertyChanged(nameof(CanResetArrangement));
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

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        OnPropertyChanged(name);
    }
}

/// <summary>배치 미리보기의 한 칸. 오버레이와 같은 아이콘(마지막으로 본 특화 코드의 것)을 쓴다.</summary>
public sealed class CooldownArrangeItem(int code, string name, ImageSource? icon) : INotifyPropertyChanged
{
    public int Code { get; } = code;
    public string Name { get; } = name;
    public ImageSource? Icon { get; } = icon;

    private bool _isDragged;
    /// <summary>지금 끌고 있는 칸 — 테두리를 강조해 무엇을 옮기는지 보이게 한다.</summary>
    public bool IsDragged
    {
        get => _isDragged;
        set
        {
            if (_isDragged == value)
            {
                return;
            }

            _isDragged = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDragged)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
