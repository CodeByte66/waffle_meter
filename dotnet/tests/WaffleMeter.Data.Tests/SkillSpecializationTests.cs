using WaffleMeter.Data;
using Xunit;

namespace WaffleMeter.Data.Tests;

/// <summary>
/// Spec for decoding a skill's specialization (특화) from its full wire code — the last four decimal
/// digits, ones digit dropped, remaining digits (1..5) name active slots.
///
/// <para>🔑 <b>Ground truth is <c>0x3900 SpecializedSkillLoad_NT</c></b>, which carries what the executor
/// actually has equipped. Against 45 sessions / 42 executor instances / 6 characters, a THREE-digit tail is
/// the build exactly — 148/152 codes, and all four exceptions are one session where the owner respecced
/// mid-capture; restricted to sessions with a single executor it is 101/101. The ones digit does not weaken
/// that: three-digit tails with a nonzero ones digit are 38/39 exact, and they carry the majority of the
/// hits (13351451 outnumbers 13351450 about 12:1). <b>So do not gate on the ones digit</b> — it indexes
/// which damage component of a cast this is, not the build.</para>
///
/// <para>⚠️ The older claim that these values were "verified against a real 151,937-hit capture" was wrong
/// attribution: that frame count came from the 후방/전방 position-byte work, and the specialization
/// arithmetic was never checked against equipment until the 0x3900 comparison above.</para>
///
/// <para>🔴 <b>Never call <see cref="SkillSpecialization.Decode(int)"/> from a test.</b> That overload asks
/// the process-wide <c>SpecCatalog.Default</c>, which <c>DataManager.LoadSkills</c> mutates — and other
/// tests in this very assembly load skills. With xUnit's default collection parallelism that is a race, and
/// it was a live one: this file's 11340028 case failed roughly one run in eight. Pass the kind explicitly.
/// </para>
/// </summary>
public sealed class SkillSpecializationTests
{
    private static int[] Slots(int rawCode)
    {
        bool[]? s = SkillSpecialization.Decode(rawCode, SpecKind.Normal);
        Assert.NotNull(s);
        var slots = new List<int>();
        for (int i = 0; i < s!.Length; i++)
        {
            if (s[i])
            {
                slots.Add(i + 1);
            }
        }

        return slots.ToArray();
    }

    [Theory]
    [InlineData(13040240, new[] { 2, 4 })]     // 024 → slots 2,4
    [InlineData(13042350, new[] { 2, 3, 5 })]  // 235 → slots 2,3,5
    [InlineData(16040340, new[] { 3, 4 })]     // 034 → slots 3,4
    [InlineData(13351351, new[] { 1, 3, 5 })]  // 135 (ones digit 1 dropped) → 1,3,5
    public void Decodes_active_slots_from_the_last_four_digits(int rawCode, int[] expected)
    {
        Assert.Equal(expected, Slots(rawCode));
    }

    /// <summary>
    /// 13351351 is 심장 찌르기, and it is the case that pins "the ones digit is not part of the build".
    /// <para>It could not be compared to equipment directly — 0x3900 only ever carries the executor's own
    /// loadout, and every actor observed firing this code was a party member. The evidence is the owner's
    /// own isomorphic codes on the same base: 심장 찌르기 read {1,5} in July and the owner fired only
    /// 13350150/13350151, then read {2,4,5} in September and fired 13352450/13352451. The tail tracked a
    /// respec two months apart, and the trailing 1 never changed what it decoded to.</para>
    /// </summary>
    [Fact]
    public void The_ones_digit_is_a_damage_component_index_not_part_of_the_build()
    {
        Assert.Equal(Slots(13351350), Slots(13351351));
        Assert.Equal(Slots(13352450), Slots(13352451));
    }

    /// <summary>
    /// 🔴 흡혈의 검 (11340000) is a STIGMA skill, so its tail is a tier, not a build.
    /// <para>This used to be pinned the other way — <c>11340028 → {2}</c> — and that was wrong twice over.
    /// The shipped catalogue only ever varies this base by a single digit (…00/10/20/30/40/50), which is
    /// what makes it a stigma; and across the whole corpus all six of the owner's characters fire this one
    /// code regardless of job, because 흡혈의 검 is the 검성 party synergy. Six different builds behind one
    /// code means the 2 cannot be anyone's build — it is 검성's stigma tier 2.</para>
    /// <para>The old case also only passed when <c>SpecCatalog.Default</c> happened not to be indexed yet;
    /// the shipping app classifies this as a stigma and returns null.</para>
    /// </summary>
    [Fact]
    public void A_stigma_tail_is_a_tier_so_it_yields_no_build()
    {
        Assert.Null(SkillSpecialization.Decode(11340028, SpecKind.Stigma));
    }

    [Fact]
    public void No_specialization_when_only_a_charge_step_is_present()
    {
        // 13720005..09: the last four digits are just a ones-digit charge step, no slot digits.
        Assert.Null(SkillSpecialization.Decode(13720005, SpecKind.Normal));
        Assert.Null(SkillSpecialization.Decode(13720009, SpecKind.Normal));
    }

    [Fact]
    public void Only_player_skills_carry_specialization()
    {
        Assert.Null(SkillSpecialization.Decode(0, SpecKind.Normal));           // no code
        Assert.Null(SkillSpecialization.Decode(100014, SpecKind.Normal));      // basic attack (below the band)
        Assert.Null(SkillSpecialization.Decode(3000119, SpecKind.Normal));     // theostone orb — decodes to garbage
        Assert.Null(SkillSpecialization.Decode(23010890, SpecKind.Normal));    // mob/boss skill (above the band)
    }

    [Fact]
    public void An_out_of_range_slot_digit_yields_no_guess()
    {
        // A digit of 6 or above isn't a valid slot (the game exposes only 5) → treat as undecodable.
        Assert.Null(SkillSpecialization.Decode(13040690, SpecKind.Normal)); // 069 → digit 6 invalid
    }

    [Fact]
    public void Floors_a_code_that_carries_trailing_framing_digits()
    {
        // Codes can arrive with extra trailing digits (>8 digits); floor to the 8-digit skill code first.
        Assert.Equal(new[] { 2, 4 }, Slots(130402401)); // → 13040240 → {2,4}
    }
}
