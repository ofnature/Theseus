using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Moq;
using Theseus.Services.Ipc;

namespace Theseus.Tests;

/// <summary>
/// Handing bosses to Minerva means making sure its auto-dodge is on — by a preset when the user
/// made one, directly when they did not — and only complaining when it is actually off.
///
/// <para>
/// Gate names are literals here on purpose, as in the Ariadne tests: a typo in the wrapper fails a
/// test instead of silently subscribing to nothing.
/// </para>
/// </summary>
public class MinervaIpcTests
{
    private sealed class Minerva
    {
        private readonly Mock<IDalamudPluginInterface> _pi = new(MockBehavior.Strict);

        public readonly HashSet<string> Presets = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Auto-dodge flags the presets carry, by name. Absent means on.</summary>
        public readonly Dictionary<string, bool> PresetDodge = new(StringComparer.OrdinalIgnoreCase);

        public bool AutoDodge;
        public string? SlotOwner;
        public readonly List<string> Applied = [];
        public readonly List<string> SwitchedOnBy = [];
        public readonly List<string> Log = [];

        public MinervaIpc Ipc { get; }

        /// <param name="modern">False for a Minerva from before the direct gates existed.</param>
        public Minerva(bool modern = true)
        {
            var apply = new Mock<ICallGateSubscriber<string, string, bool>>();
            apply.Setup(s => s.InvokeFunc(It.IsAny<string>(), It.IsAny<string>()))
                .Returns<string, string>((name, owner) =>
                {
                    if (!Presets.Contains(name))
                        return false;

                    Applied.Add(name);
                    AutoDodge = !PresetDodge.TryGetValue(name, out var on) || on;
                    SlotOwner = owner;
                    return true;
                });
            _pi.Setup(p => p.GetIpcSubscriber<string, string, bool>("Minerva.ApplyPreset")).Returns(apply.Object);

            if (modern)
            {
                var set = new Mock<ICallGateSubscriber<string, bool, bool>>();
                set.Setup(s => s.InvokeFunc(It.IsAny<string>(), It.IsAny<bool>()))
                    .Returns<string, bool>((owner, on) =>
                    {
                        if (SlotOwner is { } held && held != owner)
                            return false;

                        SlotOwner = owner;
                        AutoDodge = on;
                        SwitchedOnBy.Add(owner);
                        return true;
                    });
                _pi.Setup(p => p.GetIpcSubscriber<string, bool, bool>("Minerva.SetAutoDodge")).Returns(set.Object);

                var read = new Mock<ICallGateSubscriber<bool>>();
                read.Setup(s => s.InvokeFunc()).Returns(() => AutoDodge);
                _pi.Setup(p => p.GetIpcSubscriber<bool>("Minerva.IsAutoDodgeEnabled")).Returns(read.Object);
            }

            Ipc = new MinervaIpc(_pi.Object, Log.Add);
        }
    }

    [Fact]
    public void A_minerva_that_cannot_say_whether_the_boss_is_fighting_answers_null()
    {
        // Null, not false: false would tell the executor "not the fight" about a fight it is in.
        var minerva = new Minerva(modern: false);

        Assert.Null(minerva.Ipc.BossEngaged);
    }

    [Fact]
    public void A_preset_the_user_made_is_applied()
    {
        var minerva = new Minerva();
        minerva.Presets.Add("Theseus");

        Assert.Equal(DodgeHandoff.PresetApplied, minerva.Ipc.EnsureAutoDodge("Theseus"));
        Assert.Equal(["Theseus"], minerva.Applied);
        Assert.Empty(minerva.SwitchedOnBy);
        Assert.True(minerva.AutoDodge);
    }

    [Fact]
    public void Without_a_preset_auto_dodge_is_switched_on_directly()
    {
        // The field case: nobody had made the preset, on any machine. Requiring one turned a
        // working setup into a warning on every run and a broken one into silence.
        var minerva = new Minerva();

        Assert.Equal(DodgeHandoff.SwitchedOn, minerva.Ipc.EnsureAutoDodge("Theseus"));
        Assert.Equal(["Theseus"], minerva.SwitchedOnBy);
        Assert.True(minerva.AutoDodge);
    }

    [Fact]
    public void Auto_dodge_that_is_already_on_is_left_alone()
    {
        // Claiming the slot would push out whoever holds it for no gain at all.
        var minerva = new Minerva { AutoDodge = true, SlotOwner = "Daedalus" };

        Assert.Equal(DodgeHandoff.AlreadyOn, minerva.Ipc.EnsureAutoDodge("Theseus"));
        Assert.Empty(minerva.SwitchedOnBy);
        Assert.Equal("Daedalus", minerva.SlotOwner);
    }

    [Fact]
    public void A_preset_with_auto_dodge_off_is_still_switched_on()
    {
        var minerva = new Minerva();
        minerva.Presets.Add("Theseus");
        minerva.PresetDodge["Theseus"] = false;

        Assert.Equal(DodgeHandoff.PresetApplied, minerva.Ipc.EnsureAutoDodge("Theseus"));
        Assert.True(minerva.AutoDodge); // the preset's other settings are kept; the dodge is the point
    }

    [Fact]
    public void A_slot_someone_else_holds_with_auto_dodge_off_is_reported_as_off()
    {
        var minerva = new Minerva { AutoDodge = false, SlotOwner = "Daedalus" };

        Assert.Equal(DodgeHandoff.Off, minerva.Ipc.EnsureAutoDodge("Theseus"));
        Assert.False(minerva.AutoDodge);
    }

    [Fact]
    public void A_blank_preset_name_goes_straight_to_the_switch()
    {
        var minerva = new Minerva();

        Assert.Equal(DodgeHandoff.SwitchedOn, minerva.Ipc.EnsureAutoDodge("  "));
        Assert.Empty(minerva.Applied);
    }

    [Fact]
    public void An_older_minerva_without_the_direct_gates_cannot_be_confirmed()
    {
        var minerva = new Minerva(modern: false);

        Assert.Equal(DodgeHandoff.Unknown, minerva.Ipc.EnsureAutoDodge("Theseus"));

        // The missing gates are not "Minerva is absent": the preset gate answered.
        Assert.DoesNotContain(minerva.Log, line => line.Contains("unavailable"));
    }

    [Fact]
    public void An_older_minerva_with_the_preset_is_taken_at_its_word()
    {
        var minerva = new Minerva(modern: false);
        minerva.Presets.Add("Theseus");

        Assert.Equal(DodgeHandoff.PresetApplied, minerva.Ipc.EnsureAutoDodge("Theseus"));
    }
}
