using System.Text;

namespace WaffleMeter.Capture;

/// <summary>파티·포스(20인 공대) 멤버 한 명 — <c>Common_ForceMemberInfo</c> / <c>Common_PartyMemberInfo</c> 의 앞쪽
/// 공통 필드와 표시에 쓰는 것만 담는다.</summary>
/// <param name="Slot">파티 안 자리(<c>_number</c>, 1~5).</param>
/// <param name="EntityUid">현재 존의 엔티티 uid(<c>_key</c>). <b>다른 존에 있으면 0</b>이고, 존을 옮길 때마다 바뀐다
/// (실측 콘팡 2851 → 12208 → 7376). 그래서 사람을 지목하는 키는 이게 아니라 <see cref="Dbid"/> 다.</param>
/// <param name="Dbid">캐릭터 고정 id(<c>_dbid</c>, i64) = 하위 48비트 캐릭터 번호 + 상위 16비트 서버. 존·세션이 바뀌어도
/// 그대로다. 0x9702/0x9622 의 로스터 key 는 이 값의 하위 32비트다.</param>
/// <param name="PcId">직업 코드(<c>_pc_id</c>). <c>JobClassInfo.ConvertFromCode</c> 의 입력과 같은 공간이다.</param>
/// <param name="CombatPower">전투력(<c>_combat_power</c>). <see cref="WaffleMeter.Capture.CombatPower.IsPlausible"/> 밖이면 0.</param>
/// <param name="Login">접속 중인가(<c>_login</c>).</param>
public readonly record struct WireRosterMember(
    int Slot, int EntityUid, long Dbid, string Nickname, int PcId, long CombatPower, bool Login)
{
    /// <summary>캐릭터 서버 = dbid 상위 16비트. 0x3645·0x9702 가 싣는 그 서버다(<c>_born_server_id</c> 가 아니다 —
    /// 실측 20인 포스 전원이 born 2003 인데 박태평 dbid 서버는 2016, 핀셔는 2020 이다).</summary>
    public int Server => (int)((ulong)Dbid >> 48);
}

/// <summary>포스 안 파티 하나(<c>Common_ForcePartyInfo</c>).</summary>
/// <param name="Number">파티 번호(<c>_number</c>, 1~4).</param>
public sealed record ForceParty(long PartyKey, int Number, int LimitMember, IReadOnlyList<WireRosterMember> Members);

/// <summary>0x9602 ForceInfo_NT — 포스 전체 스냅샷.</summary>
/// <param name="LimitMember">포스 정원(<c>_limit_member</c>). 실측 20인 공대 = 20.</param>
/// <param name="Reason"><c>EPartyInfoUpdateReason</c>(1 Join · 5 Reconnect …). 진단용.</param>
public sealed record ForceSnapshot(long ForceKey, long LeaderDbid, int LimitMember, IReadOnlyList<ForceParty> Parties, int Reason);

/// <summary>0x962C ForceUpdateMemberStatus_NT 중 표시에 쓰는 것.</summary>
public readonly record struct ForceMemberStatus(long Dbid, int PcId, long CombatPower);

/// <summary>0x9622 ForceRemoveMember_NT.</summary>
/// <param name="Reason"><c>EPartyInfoUpdateReason</c>(실측 전부 3 = LeaveMember).</param>
public readonly record struct ForceMemberRemoval(long MemberDbid, long PartyKey, int Reason);

/// <summary>0x9200 PartyInfo_NT — <b>본인 파티</b>(포스라면 본인 서브파티) 전체 스냅샷.</summary>
public sealed record PartySnapshot(long PartyKey, int LimitMember, IReadOnlyList<WireRosterMember> Members, int Reason);

/// <summary>
/// 파티·포스 계열 메시지를 usmap 스키마(<c>packet-catalog-1.0.46</c>) <b>선언 순서대로</b> 걸어 읽는다.
/// 바이트 패턴으로 레코드를 찾지 않는다 — 프레임 끝까지 <b>정확히</b> 소진돼야만 받아들인다(fail-closed).
///
/// <para><b>와이어 인코딩</b>(2026-10-27 20인 공대 로그 20261027-205641, 이 계열 1,330프레임 전수 정확 소진 · 실패 0):
/// <list type="bullet">
/// <item>Int32 = u32 LE, Int64 = i64 LE, Enum/Byte = u8, Vec3 = f32×3, Str/Array = varint 길이 + 본문.</item>
/// <item>예외 둘: <c>_born_server_id</c>·<c>_current_server_id</c> 는 <b>u16</b>, <c>_hp</c>·<c>_hp_max</c> 는 <b>varint</b>.</item>
/// <item>Bool 은 <b>메시지 전체가 공유하는 비트 묶음 바이트</b>에서 한 비트씩 꺼낸다 — 앞 바이트의 8비트를 다 쓴 뒤에야
/// 새 바이트를 읽고, 그 상태는 레코드·중첩 구조를 넘어 이어진다(<see cref="CurrencyItemParser"/> 와 같은 규칙).
/// 포스 멤버는 bool 이 둘(<c>_live</c>·<c>_login</c>)이라 넷마다 한 바이트다.</item>
/// <item>구조체 앞의 <b>선택 필드 마스크</b> 바이트: ForceInfo·PartyInfo 는 <c>0x00</c>(이때 <c>_target_marker_list</c>·
/// <c>_focus_target</c>·<c>_scan_list</c>·<c>_mappin_list</c> 가 와이어에 없다), 멤버 레코드는 <c>0x03</c>(이때 파티 멤버의
/// <c>_rebirth_skill_list</c> 가 없다). <b>관측된 값만 받는다</b> — 다른 값이면 어떤 필드가 붙는지 모르므로 추측하지 않고 실패한다.</item>
/// <item>ForcePartyInfo 에는 마스크가 없다(<c>_key</c> 부터 시작한다).</item>
/// </list></para>
///
/// <para>🔑 ForceInfo 헤더의 <c>_limit_member</c> 뒤 바이트(<c>02 01 02 NN</c>)는 스키마 그대로
/// <c>_distribution_type</c>·<c>_special_distribution_type</c>·<c>_special_distribution_item_grade</c>·<c>_parties</c> 개수다.
/// 그 사이에 선언된 <c>_target_marker_list</c> 는 선두 마스크 0x00 이 뺐다 — 같은 포스의 스냅샷 3건이 마지막 바이트만
/// 02 → 04 로 바뀌고 실제 파티 수가 2 → 4 인 것이 그 경계의 차분 증거다.</para>
///
/// <para>⚠️ 구세대 클라(2026-07-27 로그)의 0x9200 은 멤버 끝에 <c>_network_latency_state</c> 가 없어 여기서 실패한다.
/// 그 프레임은 호출자가 종전 바이트스캔으로 읽는다 — 이 파서는 현행 스키마만 안다.</para>
///
/// 순수 함수, 예외를 던지지 않는다.
/// </summary>
public static class RosterRecordParser
{
    /// <summary>관측된 유일한 멤버 레코드 마스크(포스 43건 · 파티 18건 전부).</summary>
    private const byte MemberMask = 0x03;

    /// <summary>관측된 유일한 ForceInfo/PartyInfo 마스크.</summary>
    private const byte HeaderMask = 0x00;

    // 개수 상한 — 쓰레기 varint 가 루프를 키우지 않게. 포스는 4파티 × 5명, 파티는 5명이 게임 상한이다.
    private const int MaxParties = 8;
    private const int MaxPartyMembers = 10;
    private const int MaxViceLeaders = 20;
    private const int MaxStringBytes = 128;

    /// <summary>엔티티 uid 상한(StreamProcessor.MaxEntityUid 와 같은 값). 넘으면 uid 를 모르는 것으로(0) 둔다.</summary>
    private const int MaxEntityUid = 16383;

    /// <summary>0x9602 ForceInfo_NT. <paramref name="body"/> = opcode 2바이트 바로 뒤.
    /// <c>[mask=00][_key i64][_leader_dbid i64][_vice_leader_dbids][_limit_member u32][enum×3][_parties][_reason u8]</c></summary>
    public static bool TryReadForceInfo(byte[] packet, int body, out ForceSnapshot? force)
    {
        force = null;
        var r = new Reader(packet, body);
        if (r.U8() != HeaderMask)
        {
            return false;
        }

        long key = r.I64();
        long leader = r.I64();
        int vice = r.Count(MaxViceLeaders);
        for (int i = 0; i < vice; i++)
        {
            r.I64();
        }

        int limit = (int)r.U32();
        r.U8(); // _distribution_type
        r.U8(); // _special_distribution_type
        r.U8(); // _special_distribution_item_grade
        int partyCount = r.Count(MaxParties);
        var parties = new List<ForceParty>(partyCount);
        for (int i = 0; i < partyCount && !r.Failed; i++)
        {
            if (ReadForceParty(ref r) is { } p)
            {
                parties.Add(p);
            }
        }

        int reason = r.U8();
        if (!r.AtEnd || limit is <= 0 or > 40)
        {
            return false;
        }

        force = new ForceSnapshot(key, leader, limit, parties, reason);
        return true;
    }

    /// <summary>0x960A ForceAddMember_NT — <c>[_party_key i64][ForceMemberInfo]</c>.</summary>
    public static bool TryReadForceAddMember(byte[] packet, int body, out long partyKey, out WireRosterMember member)
    {
        member = default;
        var r = new Reader(packet, body);
        partyKey = r.I64();
        WireRosterMember? m = ReadForceMember(ref r);
        if (m is null || !r.AtEnd)
        {
            return false;
        }

        member = m.Value;
        return true;
    }

    /// <summary>0x961D ForceAddParty_NT — <c>[ForcePartyInfo]</c>.</summary>
    public static bool TryReadForceAddParty(byte[] packet, int body, out ForceParty? party)
    {
        var r = new Reader(packet, body);
        party = ReadForceParty(ref r);
        if (party is null || !r.AtEnd)
        {
            party = null;
            return false;
        }

        return true;
    }

    /// <summary>0x961E ForceMoveMember_NT — <c>_changes: Array&lt;ForcePartyInfo&gt;</c>. 바뀐 파티들의 <b>전체</b> 멤버
    /// 목록이 실린다(실측: 순두부 2→3파티 · 무적자 3→2파티 교환 한 건에 두 파티가 다섯 명씩 통째로 왔다).</summary>
    public static bool TryReadForceMoveMember(byte[] packet, int body, out IReadOnlyList<ForceParty> changes)
    {
        changes = Array.Empty<ForceParty>();
        var r = new Reader(packet, body);
        int count = r.Count(MaxParties);
        var list = new List<ForceParty>(count);
        for (int i = 0; i < count && !r.Failed; i++)
        {
            if (ReadForceParty(ref r) is { } p)
            {
                list.Add(p);
            }
        }

        if (!r.AtEnd || list.Count == 0)
        {
            return false;
        }

        changes = list;
        return true;
    }

    /// <summary>0x961B ForceReconnectMember_NT — <c>[ForceMemberInfo]</c>. 존을 옮긴 멤버의 새 엔티티 uid(다른 존이면 0)를 싣는다.</summary>
    public static bool TryReadForceReconnectMember(byte[] packet, int body, out WireRosterMember member)
    {
        member = default;
        var r = new Reader(packet, body);
        WireRosterMember? m = ReadForceMember(ref r);
        if (m is null || !r.AtEnd)
        {
            return false;
        }

        member = m.Value;
        return true;
    }

    /// <summary>0x9622 ForceRemoveMember_NT — <c>[_member_dbid i64][_party_key i64][_reason u8]</c> 정확히 17바이트.</summary>
    public static bool TryReadForceRemoveMember(byte[] packet, int body, out ForceMemberRemoval removal)
    {
        removal = default;
        var r = new Reader(packet, body);
        long dbid = r.I64();
        long partyKey = r.I64();
        int reason = r.U8();
        if (!r.AtEnd || !IsPlausibleDbid(dbid))
        {
            return false;
        }

        removal = new ForceMemberRemoval(dbid, partyKey, reason);
        return true;
    }

    /// <summary>0x962C ForceUpdateMemberStatus_NT — 마스크 없이 고정 순서:
    /// <c>[_dbid i64][_pc_id u32][_map_key i64][_subzone_id u32][_pos 12][_level u32][_conqueror_level u32]
    /// [_equip_item_level u32][_guild_name str][_guild_emblem_id u32][_combat_power i64][_network_latency_state u8]</c>.</summary>
    public static bool TryReadForceMemberStatus(byte[] packet, int body, out ForceMemberStatus status)
    {
        status = default;
        var r = new Reader(packet, body);
        long dbid = r.I64();
        int pcId = (int)r.U32();
        r.I64();         // _map_key
        r.U32();         // _subzone_id
        r.Skip(12);      // _pos
        r.U32();         // _level
        r.U32();         // _conqueror_level
        r.U32();         // _equip_item_level
        r.SkipString();  // _guild_name
        r.U32();         // _guild_emblem_id
        long power = r.I64();
        r.U8();          // _network_latency_state
        if (!r.AtEnd || !IsPlausibleDbid(dbid))
        {
            return false;
        }

        status = new ForceMemberStatus(dbid, pcId, CombatPower.IsPlausible(power) ? power : 0);
        return true;
    }

    /// <summary>0x9200 PartyInfo_NT — <c>[mask=00][_key i64][_leader_dbid i64][_limit_member u32][enum×4][_members][_reason u8]</c>.
    /// 포스 안에서는 본인 서브파티만 싣는다(실측 2파티 4~5명).</summary>
    public static bool TryReadPartyInfo(byte[] packet, int body, out PartySnapshot? party)
    {
        party = null;
        var r = new Reader(packet, body);
        if (r.U8() != HeaderMask)
        {
            return false;
        }

        long key = r.I64();
        r.I64();                 // _leader_dbid
        int limit = (int)r.U32();
        r.U8();                  // _distribution_type
        r.U8();                  // _special_distribution_type
        r.U8();                  // _special_distribution_item_grade
        r.U8();                  // _join_type
        int count = r.Count(MaxPartyMembers);
        var members = new List<WireRosterMember>(count);
        for (int i = 0; i < count && !r.Failed; i++)
        {
            if (ReadPartyMember(ref r) is { } m)
            {
                members.Add(m);
            }
        }

        int reason = r.U8();
        if (!r.AtEnd || members.Count != count || limit is <= 0 or > 20)
        {
            return false;
        }

        party = new PartySnapshot(key, limit, members, reason);
        return true;
    }

    /// <summary>0x920D PartyAddMember_NT — <c>[PartyMemberInfo]</c> 한 명.</summary>
    public static bool TryReadPartyAddMember(byte[] packet, int body, out WireRosterMember member)
    {
        member = default;
        var r = new Reader(packet, body);
        WireRosterMember? m = ReadPartyMember(ref r);
        if (m is null || !r.AtEnd)
        {
            return false;
        }

        member = m.Value;
        return true;
    }

    private static ForceParty? ReadForceParty(ref Reader r)
    {
        long key = r.I64();
        int number = r.U8();
        int limit = (int)r.U32();
        int count = r.Count(MaxPartyMembers);
        var members = new List<WireRosterMember>(count);
        for (int i = 0; i < count && !r.Failed; i++)
        {
            if (ReadForceMember(ref r) is { } m)
            {
                members.Add(m);
            }
        }

        if (r.Failed || members.Count != count || number is < 1 or > MaxParties || limit is <= 0 or > MaxPartyMembers)
        {
            r.Fail();
            return null;
        }

        return new ForceParty(key, number, limit, members);
    }

    /// <summary>Common_ForceMemberInfo, 선언 순서 그대로.</summary>
    private static WireRosterMember? ReadForceMember(ref Reader r)
    {
        if (!ReadMemberHead(ref r, out int slot, out int uid, out long dbid, out string? nick, out int pcId))
        {
            return null;
        }

        r.VarU64();      // _hp
        r.VarU64();      // _hp_max
        r.I64();         // _map_key
        r.U32();         // _subzone_id
        r.Bool();        // _live
        r.SkipString();  // _guild_name
        r.U32();         // _guild_emblem_id
        r.U32();         // _level
        r.U32();         // _conqueror_level
        bool login = r.Bool();
        r.U32();         // _equip_item_level
        r.Skip(12);      // _pos
        long power = r.I64();
        r.U8();          // _network_latency_state
        return MemberOrFail(ref r, slot, uid, dbid, nick!, pcId, power, login);
    }

    /// <summary>Common_PartyMemberInfo, 선언 순서 그대로(마스크 0x03 = <c>_rebirth_skill_list</c> 없음).</summary>
    private static WireRosterMember? ReadPartyMember(ref Reader r)
    {
        if (!ReadMemberHead(ref r, out int slot, out int uid, out long dbid, out string? nick, out int pcId))
        {
            return null;
        }

        r.VarU64();      // _hp
        r.VarU64();      // _hp_max
        r.Skip(24);      // _mp, _mp_max, _ap, _ap_max, _dp, _dp_max
        r.I64();         // _map_key
        r.U32();         // _subzone_id
        r.Skip(12);      // _pos
        r.Bool();        // _live
        r.SkipString();  // _guild_name
        r.U32();         // _guild_emblem_id
        r.U32();         // _level
        r.U32();         // _conqueror_level
        r.U32();         // _equip_item_level
        r.Bool();        // _can_take_drop_item
        bool login = r.Bool();
        long power = r.I64();
        r.U8();          // _network_latency_state
        return MemberOrFail(ref r, slot, uid, dbid, nick!, pcId, power, login);
    }

    /// <summary>두 멤버 구조체가 공유하는 앞부분:
    /// <c>[mask=03][_number u8][_key u32][_born_server_id u16][_current_server_id u16][_game_account_id str][_dbid i64][_nickname str][_pc_id u32]</c>.
    /// 계정 id 문자열은 읽어 넘기기만 한다(보관하지 않는다).</summary>
    private static bool ReadMemberHead(ref Reader r, out int slot, out int uid, out long dbid, out string? nick, out int pcId)
    {
        slot = uid = pcId = 0;
        dbid = 0;
        nick = null;
        if (r.U8() != MemberMask)
        {
            r.Fail();
            return false;
        }

        slot = r.U8();
        long key = r.U32();
        r.U16();         // _born_server_id
        r.U16();         // _current_server_id
        r.SkipString();  // _game_account_id
        dbid = r.I64();
        nick = r.String();
        pcId = (int)r.U32();
        uid = key is > 0 and <= MaxEntityUid ? (int)key : 0;
        if (r.Failed || slot is < 1 or > MaxPartyMembers || !IsPlausibleDbid(dbid) || !IsPlausibleNickname(nick))
        {
            r.Fail();
            return false;
        }

        return true;
    }

    private static WireRosterMember? MemberOrFail(ref Reader r, int slot, int uid, long dbid, string nick, int pcId, long power, bool login)
    {
        if (r.Failed)
        {
            return null;
        }

        return new WireRosterMember(slot, uid, dbid, nick, pcId, CombatPower.IsPlausible(power) ? power : 0, login);
    }

    /// <summary>dbid = 하위 48비트 캐릭터 번호(&gt; 0) + 상위 16비트 서버(1001~2999 대역).</summary>
    private static bool IsPlausibleDbid(long dbid)
    {
        int server = (int)((ulong)dbid >> 48);
        long id = dbid & 0xFFFF_FFFF_FFFFL;
        return id > 0 && server is (>= 1001 and < 2000) or (>= 2001 and < 3000);
    }

    /// <summary>StreamProcessor 의 닉네임 규칙(한글·영문 1자 이상, 한글·영문·숫자만)과 같다.</summary>
    private static bool IsPlausibleNickname(string? s)
    {
        if (string.IsNullOrEmpty(s) || s.Length > 30)
        {
            return false;
        }

        bool letter = false;
        foreach (char c in s)
        {
            bool hangul = c >= '가' && c <= '힣';
            bool latin = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
            if (hangul || latin)
            {
                letter = true;
            }
            else if (!char.IsDigit(c))
            {
                return false;
            }
        }

        return letter;
    }

    /// <summary>끈적한 실패 플래그를 가진 순방향 리더. 한 번 실패하면 이후 읽기는 전부 0 을 돌려주고 <see cref="Failed"/> 가
    /// 남는다 — 호출자는 마지막에 한 번만 확인하면 된다. Bool 비트 묶음 상태가 메시지 전체에 걸쳐 이어진다.</summary>
    private struct Reader
    {
        private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        private readonly byte[] _b;
        private int _o;
        private int _boolByte;
        private int _boolBits;

        public Reader(byte[] buffer, int offset)
        {
            _b = buffer;
            _o = offset;
            _boolByte = 0;
            _boolBits = 8; // 첫 Bool 에서 새 바이트를 읽는다
            Failed = offset < 0 || offset > buffer.Length;
        }

        public bool Failed { get; private set; }

        /// <summary>실패 없이 버퍼 끝에 정확히 닿았는가.</summary>
        public readonly bool AtEnd => !Failed && _o == _b.Length;

        public void Fail() => Failed = true;

        private bool Need(int n)
        {
            if (Failed || _o + n > _b.Length)
            {
                Failed = true;
                return false;
            }

            return true;
        }

        public int U8()
        {
            if (!Need(1))
            {
                return 0;
            }

            return _b[_o++];
        }

        public int U16()
        {
            if (!Need(2))
            {
                return 0;
            }

            int v = _b[_o] | (_b[_o + 1] << 8);
            _o += 2;
            return v;
        }

        public long U32()
        {
            if (!Need(4))
            {
                return 0;
            }

            long v = PacketPrimitives.ReadUInt32LeAsLong(_b, _o);
            _o += 4;
            return v;
        }

        public long I64()
        {
            if (!Need(8))
            {
                return 0;
            }

            long v = PacketPrimitives.ReadUInt64Le(_b, _o);
            _o += 8;
            return v;
        }

        public void Skip(int n)
        {
            if (Need(n))
            {
                _o += n;
            }
        }

        public long VarU64()
        {
            if (Failed)
            {
                return 0;
            }

            VarLongOutput v = PacketPrimitives.ReadVarUInt64(_b, _o);
            if (v.Length <= 0)
            {
                Failed = true;
                return 0;
            }

            _o += v.Length;
            return v.Value;
        }

        /// <summary>배열 길이 varint. <paramref name="max"/> 를 넘으면 실패.</summary>
        public int Count(int max)
        {
            if (Failed)
            {
                return 0;
            }

            VarIntOutput v = PacketPrimitives.ReadVarInt(_b, _o);
            if (v.Length <= 0 || v.Value < 0 || v.Value > max)
            {
                Failed = true;
                return 0;
            }

            _o += v.Length;
            return v.Value;
        }

        public bool Bool()
        {
            if (_boolBits >= 8)
            {
                _boolByte = U8();
                _boolBits = 0;
            }

            bool bit = ((_boolByte >> _boolBits) & 1) != 0;
            _boolBits++;
            return bit;
        }

        public string? String()
        {
            int len = Count(MaxStringBytes);
            if (!Need(len))
            {
                return null;
            }

            try
            {
                string s = StrictUtf8.GetString(_b, _o, len);
                _o += len;
                return s;
            }
            catch (ArgumentException)
            {
                Failed = true;
                return null;
            }
        }

        public void SkipString()
        {
            int len = Count(MaxStringBytes);
            Skip(len);
        }
    }
}
