using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace Theseus.Services.Fleet;

/// <summary>
/// Cross-client Theseus messaging over the Daedalus LAN relay
/// (<c>Daedalus.Relay.Publish</c> / <c>Daedalus.Relay.Message</c>).
///
/// <para>
/// The relay ferries opaque <c>{channel, json}</c> frames over Daedalus' coordination bus, reaching
/// other machines and same-machine sibling game clients alike. The three things every caller has to
/// respect, all of them learned in Charon: the publisher never hears its own frame, publishing is a
/// silent no-op while Daedalus is absent, and messages arrive on the framework thread.
/// </para>
///
/// <para>
/// Everything here fails open. A fleet feature that cannot reach its peers must degrade to the solo
/// behaviour it already has, never to a wait for a message that will not come.
/// </para>
/// </summary>
public sealed class RelayClient : IDisposable
{
    /// <summary>Who is taking which object, and what happened to it (§5.4).</summary>
    public const string ClaimChannel = "theseus.claim";

    private readonly ICallGateSubscriber<string, string, object?> _publish;
    private readonly ICallGateSubscriber<string, string, object?> _message;
    private readonly IPluginLog _log;

    private bool _warned;

    /// <summary>Raised on the framework thread for every relay frame from another client.</summary>
    public event Action<string, string>? OnMessage;

    public RelayClient(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        _log = log;

        try
        {
            _publish = pluginInterface.GetIpcSubscriber<string, string, object?>("Daedalus.Relay.Publish");
            _message = pluginInterface.GetIpcSubscriber<string, string, object?>("Daedalus.Relay.Message");
            _message.Subscribe(OnRelayMessage);
            Available = true;
        }
        catch (Exception ex)
        {
            // No Daedalus, or no relay: everything over this client degrades to solo behaviour.
            Available = false;
            _log.Information($"Theseus: Daedalus relay unavailable ({ex.Message}) — fleet claims stay local.");
            _publish = null!;
            _message = null!;
        }
    }

    /// <summary>False when the relay could not be wired at all. Claiming still works, locally.</summary>
    public bool Available { get; }

    /// <summary>Broadcast to every other client. Fail-open no-op when Daedalus is absent.</summary>
    public void Publish(string channel, string json)
    {
        if (!Available)
            return;

        try
        {
            _publish.InvokeAction(channel, json);
        }
        catch (Exception ex)
        {
            // A dropped frame is a claim that never reaches the fleet, which is exactly what the
            // local board does when solo. Worth one line, not worth a second attempt in a loop.
            if (_warned)
                return;

            _warned = true;
            _log.Warning($"Theseus: relay publish failed ({ex.Message}) — fleet claims may be duplicated.");
        }
    }

    private void OnRelayMessage(string channel, string json)
    {
        try
        {
            OnMessage?.Invoke(channel, json);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Theseus: relay message handler failed (channel '{0}')", channel);
        }
    }

    public void Dispose()
    {
        if (!Available)
            return;

        try
        {
            _message.Unsubscribe(OnRelayMessage);
        }
        catch
        {
            // Unloading anyway.
        }
    }
}
