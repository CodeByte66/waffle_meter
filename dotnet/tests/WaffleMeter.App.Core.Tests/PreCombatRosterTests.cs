using WaffleMeter.App.Core;
using WaffleMeter.Capture;
using WaffleMeter.Data;
using Xunit;
using F = WaffleMeter.Capture.Tests.ForceRosterFixtures;

namespace WaffleMeter.App.Core.Tests;

/// <summary>
/// 전투 전 파티 프리뷰 조립(<see cref="PreCombatRoster"/>) — 종전엔 App.Wpf 안에 있어 테스트가 없었다.
/// <list type="bullet">
/// <item><b>종전 동작 보존</b>: 0x9702 방 로스터·일반 파티·전투 기여자 폴백 시나리오는 <see cref="LegacyBuild"/>(dev 의
/// App.xaml.cs 조립을 그대로 옮긴 오라클)와 행 순서·id·직업·전투력까지 같아야 한다 — 포스 데이터가 같이 있어도.</item>
/// <item><b>A</b> 0x9200 행 = 저장소 User(직업·전투력), 본인은 본인 User. <b>B</b> 다른 존(uid 0) 파티원 = 이름 행.
/// <b>C</b> 20인 공대 = 본인 파티만, 직업·전투력 포함, 전투력은 나중에 받은 값.</item>
/// <item>실측 프레임(20261027-205641)을 StreamProcessor → DataManager → 조립까지 통째로 흘린 결과.</item>
/// </list>
/// </summary>
public sealed class PreCombatRosterTests
{
    private const int Srv = 2003;
    private const long Ttl = 300_000;
    private const long OwnKey = 0x24135ED200149019L, OtherKey = 0x24135ED200148F37L;

    private sealed class World
    {
        public readonly Dictionary<int, User> Users = new();
        public readonly Dictionary<int, long> PowerAt = new();
        public int Exec;
        public List<User> Authoritative = [];
        public List<(string, int, int)> RosterIds = [];
        public List<(int, string, int)> Profiles = [];
        public List<(string, int)> OffZone = [];
        public List<int> Recent = [];
        public List<(string, int, int, int)> JobPower = [];
        public ForceRosterView? Force;
        public long OwnPartyKey;

        public User Add(int uid, string nick, JobClass? job = null, int power = 0, long powerAt = 0, int server = Srv)
        {
            var u = new User(uid, nick, server, job, isExecutor: uid == Exec, power: power);
            Users[uid] = u;
            if (powerAt > 0)
            {
                PowerAt[uid] = powerAt;
            }

            return u;
        }

        public PreCombatRosterInputs Inputs() => new(
            Exec, Authoritative, RosterIds, Profiles, OffZone, Recent, JobPower, Force, OwnPartyKey,
            uid => Users.GetValueOrDefault(uid),
            (nick, server) => Users.Values.FirstOrDefault(u => u.Nickname == nick && u.Server == server),
            uid => PowerAt.GetValueOrDefault(uid));
    }

    private static string Show(IEnumerable<User> rows) =>
        string.Join(" | ", rows.Select(u => $"{u.Id}:{u.Nickname}:{u.Job?.ToString() ?? "-"}:{u.Power}"));

    private static ForceRosterMember FM(string nick, int uid, int slot, int pc, long power, long at, long key = OwnKey, long id = 0) =>
        new(((long)Srv << 48) | (id == 0 ? 1000 + slot + uid : id), key, slot, uid, nick, Srv, pc, power, at);

    private static ForceRosterView Force(int limit, params ForceRosterParty[] parties) => new(1, limit, 1, parties);

    /// <summary>10인 공대 1파티 — 로스터에 다섯, 그중 하나는 uid 를 못 푼 이름.</summary>
    private static World TenManRaid(bool withForce, int forceLimit = 20)
    {
        var w = new World { Exec = 11 };
        User self = w.Add(11, "나", JobClass.CLERIC, 700000);
        User a = w.Add(12, "가", JobClass.RANGER, 900000);
        User b = w.Add(13, "나비", JobClass.TEMPLAR, 800000);
        w.Add(99, "남", JobClass.SORCERER, 999999); // 지난 파티원 — 현재 로스터에 없음
        w.Authoritative = [self, a, b];
        w.RosterIds = [("나", Srv, 1), ("가", Srv, 2), ("나비", Srv, 3), ("다람", Srv, 4), ("라", Srv, 5)];
        w.Profiles = [(12, "가", Srv), (77, "다람", Srv), (99, "남", Srv)];
        w.Recent = [13, 99, 55];
        w.JobPower = [("다람", Srv, 24, 650000), ("라", Srv, 0, 0), ("가", Srv, 16, 1)];
        if (withForce)
        {
            w.Force = Force(forceLimit,
                new ForceRosterParty(OwnKey, 1, [FM("나", 11, 1, 32, 1, 5), FM("가", 12, 2, 16, 1, 5)]));
            w.OwnPartyKey = OwnKey;
        }

        return w;
    }

    [Theory]
    [InlineData(false, 20)]
    [InlineData(true, 20)]  // 10인 공대도 포스다 — 그래도 0x9702 방 로스터가 살아 있으면 종전 그대로
    [InlineData(true, 10)]
    public void A_ten_man_raid_through_the_room_roster_is_byte_for_byte_the_old_preview(bool withForce, int limit)
    {
        World w = TenManRaid(withForce, limit);
        Assert.Equal(Show(LegacyBuild(w)), Show(PreCombatRoster.Build(w.Inputs())));
    }

    [Fact]
    public void A_plain_party_from_combat_contributors_only_is_the_old_preview()
    {
        var w = new World { Exec = 1 };
        w.Add(1, "나", JobClass.RANGER, 500000);
        w.Add(2, "둘", JobClass.CLERIC, 600000);
        w.Add(3, "셋");
        w.Recent = [3, 2, 404];
        Assert.Equal(Show(LegacyBuild(w)), Show(PreCombatRoster.Build(w.Inputs())));
        Assert.Equal("1:나:RANGER:500000 | 2:둘:CLERIC:600000 | 3:셋:-:0", Show(PreCombatRoster.Build(w.Inputs())));
    }

    [Fact]
    public void A_purely_self_injected_preview_is_still_suppressed()
    {
        var w = new World { Exec = 1 };
        w.Add(1, "나", JobClass.RANGER, 500000);
        Assert.Empty(PreCombatRoster.Build(w.Inputs()));
        Assert.Empty(LegacyBuild(w));
    }

    [Fact]
    public void A_profile_rows_carry_the_stored_job_and_power_and_self_is_the_self_user()
    {
        // 실측 증상: 0x9200 로만 아는 파티원이 빈 User 로 들어가 직업 아이콘·전투력이 비고, 본인은 빈 객체가 먼저 자리 잡아
        // 본인 주입이 건너뛰어졌다.
        var w = new World { Exec = 2851 };
        w.Add(2851, "콘팡", JobClass.RANGER, 883941);
        w.Add(5565, "쉰", JobClass.RANGER, 854294);
        w.Add(7423, "읓"); // 저장소가 이름만 안다
        w.Add(8000, "다른사람", JobClass.CHANTER, 1); // uid 재사용 — 프로필 이름과 다르면 저장소 값을 쓰지 않는다
        w.Profiles = [(5565, "쉰", Srv), (7423, "읓", Srv), (2851, "콘팡", Srv), (8000, "Violette", Srv)];

        List<User> rows = PreCombatRoster.Build(w.Inputs());
        Assert.Equal("2851:콘팡:RANGER:883941 | 5565:쉰:RANGER:854294 | 7423:읓:-:0 | 8000:Violette:-:0", Show(rows));
        Assert.Same(w.Users[2851], rows[0]);
        Assert.NotEqual(Show(LegacyBuild(w)), Show(rows)); // 종전엔 쉰·콘팡이 빈 행이었다
    }

    [Fact]
    public void B_a_party_member_in_another_zone_shows_by_name_with_whatever_the_store_knows()
    {
        var w = new World { Exec = 2851 };
        w.Add(2851, "콘팡", JobClass.RANGER, 883941);
        w.Add(4000, "Violette", JobClass.SORCERER, 893426); // 지난 존에서 본 그 사람(uid 는 낡았다)
        w.Profiles = [(2851, "콘팡", Srv)];
        w.OffZone = [("Violette", Srv), ("헐", Srv)];

        List<User> rows = PreCombatRoster.Build(w.Inputs());
        Assert.Equal("2851:콘팡:RANGER:883941 | -1:Violette:SORCERER:893426 | -2:헐:-:0", Show(rows));
    }

    [Fact]
    public void B_off_zone_names_respect_the_current_room_roster_filter()
    {
        World w = TenManRaid(withForce: false);
        w.OffZone = [("다람", Srv), ("떠난이", Srv)];
        List<User> rows = PreCombatRoster.Build(w.Inputs());
        Assert.DoesNotContain(rows, u => u.Nickname == "떠난이");
        Assert.Single(rows, u => u.Nickname == "다람");
    }

    /// <summary>20인 공대: 1파티(남의 파티) 다섯 + 2파티(내 파티) 다섯.</summary>
    private static World TwentyManRaid(bool selfKnown)
    {
        var w = new World { Exec = selfKnown ? 2851 : 0 };
        if (selfKnown)
        {
            w.Add(2851, "콘팡", JobClass.RANGER, 870000, powerAt: 100);
        }

        w.Add(5565, "쉰", JobClass.RANGER, 854294, powerAt: 50);
        w.Add(9114, "무적자", null, 700000, powerAt: 900); // 저장소 값이 포스 값보다 나중 — 이게 이긴다
        w.Add(14527, "콩령", JobClass.ELEMENTALIST, 1030271);
        w.Force = Force(20,
            new ForceRosterParty(OtherKey, 1,
            [
                FM("콩령", 14527, 1, 24, 1030271, 10, OtherKey), FM("마의", 2122, 2, 32, 771847, 10, OtherKey),
                FM("KMH", 833, 3, 36, 895544, 10, OtherKey), FM("롤형", 1044, 4, 16, 716328, 10, OtherKey),
                FM("대황", 14986, 5, 8, 822980, 10, OtherKey),
            ]),
            new ForceRosterParty(OwnKey, 2,
            [
                FM("Violette", 0, 1, 28, 1024066, 200), FM("읓", 7423, 2, 20, 1056174, 200),
                FM("쉰", 5565, 3, 16, 860000, 300), FM("콘팡", 2851, 4, 16, 883941, 200),
                FM("무적자", 9114, 5, 36, 882255, 300),
            ]));
        w.Profiles = [(5565, "쉰", Srv), (2851, "콘팡", Srv), (14527, "콩령", Srv)];
        w.Recent = [14527, 2122];
        w.OwnPartyKey = OwnKey;
        return w;
    }

    [Fact]
    public void C_a_twenty_man_raid_shows_only_my_party_with_job_and_latest_power_self_first()
    {
        World w = TwentyManRaid(selfKnown: true);
        List<User> rows = PreCombatRoster.Build(w.Inputs());
        Assert.Equal(
            "2851:콘팡:RANGER:883941 | 7423:읓:ASSASSIN:1056174 | -1:Violette:SORCERER:1024066 | 5565:쉰:RANGER:860000 | 9114:무적자:CHANTER:700000",
            Show(rows));
        // 저장소 User 는 건드리지 않는다(표시용 사본).
        Assert.Equal(870000, w.Users[2851].Power);
        Assert.Null(w.Users[9114].Job);
    }

    [Fact]
    public void C_without_a_recognized_self_my_party_comes_from_the_0x9200_party_key()
    {
        // 미터를 공대 도중에 켜면 0x3633 이 다시 안 와 본인 uid 를 모른다(실측). 0x9200 의 파티 key 가 내 서브파티다.
        World w = TwentyManRaid(selfKnown: false);
        List<User> rows = PreCombatRoster.Build(w.Inputs());
        Assert.Equal(new[] { "읓", "Violette", "콘팡", "쉰", "무적자" }, rows.Select(u => u.Nickname));

        w.OwnPartyKey = 0; // 내 파티를 고를 근거가 없으면 포스를 무시한다(종전 경로)
        Assert.Equal(Show(WithoutForce(w)), Show(PreCombatRoster.Build(w.Inputs())));
    }

    private static List<User> WithoutForce(World w)
    {
        ForceRosterView? keep = w.Force;
        w.Force = null;
        List<User> rows = PreCombatRoster.Build(w.Inputs());
        w.Force = keep;
        return rows;
    }

    [Fact]
    public void C_the_store_power_wins_only_when_it_arrived_later()
    {
        World w = TwentyManRaid(selfKnown: true);
        w.PowerAt[9114] = 299; // 이제 포스(300)가 나중
        Assert.Contains(PreCombatRoster.Build(w.Inputs()), u => u.Nickname == "무적자" && u.Power == 882255);
        w.PowerAt[9114] = 301;
        Assert.Contains(PreCombatRoster.Build(w.Inputs()), u => u.Nickname == "무적자" && u.Power == 700000);
    }

    [Fact]
    public void C_a_ten_member_force_keeps_the_old_preview()
    {
        World w = TwentyManRaid(selfKnown: true);
        w.Force = w.Force! with { LimitMember = 10 };
        List<User> rows = PreCombatRoster.Build(w.Inputs());
        Assert.Equal(Show(WithoutForce(w)), Show(rows));
        Assert.Contains(rows, u => u.Nickname == "콩령"); // 남의 파티원(최근 전투 기여자)도 종전처럼 섞인다
    }

    [Fact]
    public void C_a_live_room_roster_turns_the_raid_rule_off()
    {
        World w = TwentyManRaid(selfKnown: true);
        w.RosterIds = [("콘팡", Srv, 1), ("쉰", Srv, 2)];
        Assert.Equal(Show(WithoutForce(w)), Show(PreCombatRoster.Build(w.Inputs())));
    }

    [Fact]
    public void Real_frames_end_to_end_show_party_two_with_job_and_power()
    {
        long clock = 1_793_102_309_233; // 20:58:29.233 KST
        var data = new DataManager { Clock = () => clock };
        var sp = new StreamProcessor(null, data, null);
        sp.OnPacketReceived(F.Bytes(F.PartyInfo205829), clock);
        sp.OnPacketReceived(F.Bytes(F.ForceInfo205829), clock);
        clock += 96;
        sp.OnPacketReceived(F.Bytes(F.ForceReconnect205829), clock);

        List<User> rows = PreCombatRoster.Build(PreCombatRoster.Gather(data, [], [], clock, Ttl));
        Assert.Equal(
            "7423:읓:ASSASSIN:1056174 | 7982:Violette:SORCERER:1024066 | 2851:콘팡:RANGER:883941 | 5565:쉰:RANGER:854294",
            Show(rows));

        clock += 7_351; // 20:58:36.680 순두부 합류
        sp.OnPacketReceived(F.Bytes(F.PartyAddMember205836), clock);
        sp.OnPacketReceived(F.Bytes(F.ForceAddMember205836), clock);
        rows = PreCombatRoster.Build(PreCombatRoster.Gather(data, [], [], clock, Ttl));
        Assert.Equal(new[] { "읓", "Violette", "콘팡", "쉰", "순두부" }, rows.Select(u => u.Nickname));
        Assert.Equal(JobClass.ELEMENTALIST, rows[4].Job);

        // 같은 시점의 종전 조립은 0x9200 의 uid 있는 세 명만, 직업·전투력 없이 보였다(제보된 증상).
        var w = new World
        {
            Profiles = data.MemberProfileRoster(Ttl).ToList(),
        };
        Assert.All(LegacyBuild(w), u => Assert.Equal(0, u.Power));
        Assert.Equal(4, LegacyBuild(w).Count); // 쉰·읓·콘팡·순두부 — Violette(다른 존)·직업·전투력 없음
    }

    /// <summary>dev(552a350b) App.xaml.cs 749-902 조립을 그대로 옮긴 오라클 — 비교 기준.</summary>
    private static List<User> LegacyBuild(World w)
    {
        int execUid = w.Exec;
        var rosterById = new Dictionary<int, User>();
        foreach (User member in w.Authoritative)
        {
            rosterById[member.Id] = member;
        }

        var currentPartyIds = new HashSet<(string, int)>(w.RosterIds.Select(m => (m.Item1, m.Item2)));
        bool filterToCurrentParty = currentPartyIds.Count > 0;
        foreach ((int mpUid, string mpNick, int mpServer) in w.Profiles)
        {
            if (!rosterById.ContainsKey(mpUid) && !string.IsNullOrWhiteSpace(mpNick)
                && (!filterToCurrentParty || currentPartyIds.Contains((mpNick, mpServer))))
            {
                rosterById[mpUid] = new User(mpUid, mpNick, mpServer);
            }
        }

        foreach (int uid in w.Recent)
        {
            if (rosterById.ContainsKey(uid))
            {
                continue;
            }

            User? u = w.Users.GetValueOrDefault(uid);
            if (u != null && !string.IsNullOrWhiteSpace(u.Nickname)
                && (!filterToCurrentParty || u.Id == execUid || currentPartyIds.Contains((u.Nickname!, u.Server))))
            {
                rosterById[uid] = u;
            }
        }

        bool hasPartySource = rosterById.Count > 0;
        if (execUid != 0 && !rosterById.ContainsKey(execUid))
        {
            User? self = w.Users.GetValueOrDefault(execUid);
            if (self != null && !string.IsNullOrWhiteSpace(self.Nickname))
            {
                rosterById[execUid] = self;
            }
        }

        List<User> partyRoster = rosterById.Values.OrderByDescending(u => u.Id == execUid).ThenByDescending(u => u.Power).ToList();
        var seenIdentity = new HashSet<(string, int)>();
        partyRoster = partyRoster.Where(u => string.IsNullOrWhiteSpace(u.Nickname) || seenIdentity.Add((u.Nickname!, u.Server))).ToList();
        if (!hasPartySource && partyRoster.Count == 1 && partyRoster[0].Id == execUid)
        {
            partyRoster.Clear();
        }

        if (partyRoster.Count > 0 || hasPartySource)
        {
            var shown = new HashSet<(string, int)>(partyRoster.Where(u => !string.IsNullOrWhiteSpace(u.Nickname)).Select(u => (u.Nickname!, u.Server)));
            int placeholderId = -1;
            foreach ((string rNick, int rServer, int _) in w.RosterIds)
            {
                if (!string.IsNullOrWhiteSpace(rNick) && shown.Add((rNick, rServer)))
                {
                    partyRoster.Add(new User(placeholderId--, rNick, rServer));
                }
            }
        }

        var rosterJp = new Dictionary<(string, int), (JobClass? Job, int Power)>();
        foreach ((string n, int s, int jc, int pw) in w.JobPower)
        {
            if (!string.IsNullOrWhiteSpace(n))
            {
                rosterJp[(n, s)] = (JobClassInfo.ConvertFromCode(jc), pw);
            }
        }

        if (rosterJp.Count > 0)
        {
            for (int i = 0; i < partyRoster.Count; i++)
            {
                User u = partyRoster[i];
                if (u.Id == execUid || string.IsNullOrWhiteSpace(u.Nickname) || (u.Job != null && u.Power > 0)
                    || !rosterJp.TryGetValue((u.Nickname!, u.Server), out (JobClass? Job, int Power) jp))
                {
                    continue;
                }

                User c = u.Copy();
                if (c.Job == null && jp.Job != null)
                {
                    c.Job = jp.Job;
                    c.JobSource = JobProvenance.Authoritative;
                }

                if (c.Power <= 0 && jp.Power > 0)
                {
                    c.Power = jp.Power;
                }

                partyRoster[i] = c;
            }
        }

        return partyRoster;
    }
}
