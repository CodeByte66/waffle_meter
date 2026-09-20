using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WaffleMeter.Data;

/// <summary>
/// 전투 기록을 디스크에 남기는 계층. <see cref="BattleLogRepository"/> 가 메모리에서 하던 일(병합·정원·초기화)을
/// 그대로 파일 한 벌로 비춘다 — 항목 하나 = 파일 하나(<c>&lt;id&gt;.json.gz</c>).
///
/// <para><b>왜 항목마다 파일인가.</b> 한 덩어리 파일이면 전투가 끝날 때마다 60건 전부를 다시 써야 한다(수 MB).
/// 항목별이면 저장은 한 건 쓰기, 병합은 한 건 덮어쓰기, 정원 초과는 한 건 삭제다.</para>
///
/// <para><b>비용(실측).</b> 10인 공대 200초 전투 하나 — 사람당 스킬 40·버프 60·시전 400·초당 버킷 200 — 가
/// <b>47 KiB</b>(gzip), 쓰기 <b>3.4 ms</b>. 정원을 꽉 채워도 2.8 MiB라 사용자 폴더에 부담이 없고,
/// 전투 종료 틱에 한 번 묻는 3 ms 는 같은 지점의 다른 일(업로드 큐·버프 저장소 prune)과 같은 차원이다.</para>
///
/// <para><b>왜 실패를 삼키는가.</b> 기록 영속화는 <b>편의</b>다. 디스크가 가득 찼거나 폴더가 잠겼다고 해서
/// 전투 집계가 멈추면 안 된다 — 모든 진입점이 예외를 먹고, 최악의 경우 동작은 영속화 이전(메모리 전용)과 같다.</para>
///
/// <para><b>⚠ 포맷 버전.</b> 파일마다 <c>schema</c> 를 싣고, 이 빌드가 아는 번호가 아니면 <b>그 파일만</b>
/// 건너뛴다. 모델이 바뀌면 옛 파일은 조용히 사라지는 셈인데, 반쯤 읽힌 전투를 화면에 올리는 것보다 낫다 —
/// 그건 "기록이 이상하다"로 읽히고 원인을 짚을 단서가 없다.</para>
/// </summary>
public sealed class BattleHistoryStore(string directory)
{
    /// <summary>현재 포맷. <see cref="DpsReport"/> 계열 모델이 바뀌면 올린다.</summary>
    public const int SchemaVersion = 1;

    private const string Extension = ".json.gz";

    private static readonly JsonSerializerOptions Json = new()
    {
        // 열거형은 이름으로 싣는다. 숫자로 실으면 나중에 JobClass 에 값이 끼어드는 날 옛 기록이 조용히
        // 다른 직업으로 읽힌다 — 틀린 걸 알아챌 방법이 없는 종류의 손상이다.
        Converters = { new JsonStringEnumConverter(), new SpanTupleConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Directory { get; } = directory;

    /// <summary>디스크에 있는 전투를 오래된 것부터 돌려준다. 읽을 수 없는 파일은 건너뛴다(빈 목록도 정상).</summary>
    public List<(long Id, DpsLog Log)> Load()
    {
        var loaded = new List<(long Id, DpsLog Log)>();
        string[] files;
        try
        {
            files = System.IO.Directory.Exists(Directory)
                ? System.IO.Directory.GetFiles(Directory, "*" + Extension)
                : [];
        }
        catch
        {
            return loaded;
        }

        foreach (string file in files)
        {
            if (!long.TryParse(Path.GetFileName(file)[..^Extension.Length], out long id))
            {
                continue;
            }

            if (Read(file) is { } log)
            {
                loaded.Add((id, log));
            }
        }

        // 파일 이름은 저장 순서대로 커지는 번호다 — 정렬이 곧 기록 순서(오래된 것 먼저)다.
        loaded.Sort((a, b) => a.Id.CompareTo(b.Id));
        return loaded;
    }

    public void Write(long id, DpsLog log)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            string path = PathFor(id);
            // 임시 파일 → 이동. 쓰는 도중에 프로세스가 죽어도 반쯤 쓰인 파일이 남지 않는다(다음 기동에서
            // 그 전투 하나만 조용히 사라지는 대신, 읽다 실패하는 파일이 매번 남는 쪽이 더 나쁘다).
            string temp = path + ".tmp";
            using (FileStream raw = File.Create(temp))
            using (var gz = new GZipStream(raw, CompressionLevel.Fastest))
            {
                JsonSerializer.Serialize(gz, new StoredBattle(SchemaVersion, log.Report, log.SummonMap), Json);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            // 영속화 실패는 집계를 막지 않는다.
        }
    }

    public void Delete(long id)
    {
        try
        {
            File.Delete(PathFor(id));
        }
        catch
        {
            // 이미 없거나 잠겼다 — 다음 기동에서 정원 규칙이 다시 정리한다.
        }
    }

    /// <summary>초기화. 우리가 쓴 파일만 지운다 — 폴더를 통째로 지우면 남이 둔 것까지 같이 날아간다.</summary>
    public void Clear()
    {
        try
        {
            if (!System.IO.Directory.Exists(Directory))
            {
                return;
            }

            foreach (string file in System.IO.Directory.GetFiles(Directory, "*" + Extension))
            {
                try
                {
                    File.Delete(file);
                }
                catch
                {
                    // 개별 실패는 넘어간다 — 나머지라도 지운다.
                }
            }
        }
        catch
        {
            // 폴더 자체를 못 읽는다 — 메모리 쪽은 이미 비워졌으니 그대로 둔다.
        }
    }

    private string PathFor(long id) => Path.Combine(Directory, id.ToString(System.Globalization.CultureInfo.InvariantCulture) + Extension);

    private static DpsLog? Read(string path)
    {
        try
        {
            using FileStream raw = File.OpenRead(path);
            using var gz = new GZipStream(raw, CompressionMode.Decompress);
            StoredBattle? stored = JsonSerializer.Deserialize<StoredBattle>(gz, Json);
            if (stored?.Report is not { } report || stored.Schema != SchemaVersion)
            {
                return null;
            }

            // DpsLog 의 세 딕셔너리는 저장 시점에 리포트의 동결 사본과 <b>같은 객체</b>다(DataManager.SaveBattleLog).
            // 그래서 파일에는 리포트만 싣고, 여기서 다시 묶어 준다 — 두 번 실으면 파일이 두 배가 된다.
            return new DpsLog
            {
                Report = report,
                SummonMap = stored.SummonMap ?? new Dictionary<int, int>(),
                Packets = [],                                   // 원시 패킷은 저장 시점에도 비어 있다
                SkillDetails = report.SkillDetailsSnapshot,
                BuffRates = report.BuffRates,
                BossBuffRates = report.BossBuffRates,
            };
        }
        catch
        {
            return null; // 잘렸거나, 옛 모델이거나, 우리 파일이 아니다
        }
    }

    private sealed record StoredBattle(int Schema, DpsReport? Report, Dictionary<int, int>? SummonMap);

    /// <summary>
    /// <c>(long Start, long End)</c> 를 <c>[start,end]</c> 로 싣는다.
    /// <para>⚠ ValueTuple 은 프로퍼티가 아니라 <b>필드</b>라, 기본 설정의 System.Text.Json 은 이걸 <c>{}</c> 로
    /// 쓴다 — 버프 구간이 통째로 사라진 채 아무 오류도 없이 저장된다(<see cref="RosterMember"/> 주석의 그
    /// 함정과 같은 것). <c>IncludeFields</c> 를 켜는 대신 여기만 다루는 이유는 두 가지다: 전역 설정이라
    /// 다른 타입의 필드까지 딸려 나가고, <c>{"Item1":..}</c> 는 구간 하나에 20바이트를 더 쓴다.</para>
    /// </summary>
    private sealed class SpanTupleConverter : JsonConverter<(long Start, long End)>
    {
        public override (long Start, long End) Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray)
            {
                throw new JsonException("span must be [start,end]");
            }

            reader.Read();
            long start = reader.GetInt64();
            reader.Read();
            long end = reader.GetInt64();
            reader.Read(); // EndArray
            return (start, end);
        }

        public override void Write(Utf8JsonWriter writer, (long Start, long End) value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(value.Start);
            writer.WriteNumberValue(value.End);
            writer.WriteEndArray();
        }
    }
}
