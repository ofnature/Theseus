namespace Theseus.Config;

/// <summary>
/// Who computes paths.
///
/// <para>
/// Both are normally installed, and whichever is <i>not</i> selected is the fallback rather than
/// dead weight: a move the selected source cannot answer is handed to the other one, once, so a
/// run never stalls on the source itself. Ariadne is not an upgrade to vnavmesh in every
/// dimension — that is what the per-territory flip is for, cutting one duty at a time.
/// </para>
/// </summary>
public enum NavSource
{
    /// <summary>
    /// vnavmesh, in process, today's behaviour: the mesh is built in game, so the first entry into
    /// a zone waits 7–15 s before anything can be routed.
    /// </summary>
    Vnavmesh,

    /// <summary>
    /// Ariadne, answered out of process by Mnemosyne: this zone's mesh is already in a store, so
    /// routing is possible about a tenth of a second after zone-in instead of after a build.
    /// </summary>
    Ariadne,
}
