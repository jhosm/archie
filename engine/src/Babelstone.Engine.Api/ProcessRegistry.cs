using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Babelstone.Engine.Api;

/// <summary>
/// The in-host process tracker behind the asynchronous command surface (I.1, bd babelstone-pxj9;
/// the 202-Accepted + process_id + SSE contract of <see href="../../../../docs/product-management/integration_concepts/adrs/ADR-IC-006-edge-api-gateway.md">ADR-IC-006</see>
/// and Document 05 §Step-0). A command POST does NOT block on completion: the host assigns a
/// <c>process_id</c>, dispatches the command through the engine command path on a background task,
/// returns <c>202 Accepted</c> with a <c>stream_url</c>, and records each lifecycle change here. The
/// SSE endpoint (<see cref="ProcessStreamEndpoints"/>) replays the current snapshot then streams
/// subsequent updates until the process reaches a terminal state.
/// </summary>
/// <remarks>
/// <para>
/// This is FAMILY-AGNOSTIC host-shell infrastructure (ADR-PC-021 §D2/§P2): it tracks an opaque
/// command dispatch, never a term-deposit type, so a second family reuses it unchanged. It is the
/// engine-host's own lightweight async-command bookkeeping — NOT the cross-context constitution saga
/// (Epic H, <c>orchestrator/</c>), whose durable <c>saga_state</c> table and compensation choreography
/// live in a separate bounded context this host does not reference.
/// </para>
/// <para>
/// Purity boundary: the registry lives in the impure host shell, where stamping wall-clock instants
/// and minting ids is allowed — exactly as the existing endpoints stamp <c>clock.GetUtcNow()</c>
/// (ADR-PC-010 §P5). No clock, randomness, or I/O leaks into any pure decider/fold: the dispatch
/// closure the registry runs calls the SAME <c>TermDepositConstitutionService</c> kernel path the
/// synchronous POST uses, so the engine's event-sourcing discipline is untouched.
/// </para>
/// <para>
/// v1 keeps process state in-memory (a single-host dev boundary, auth deferred to Epic J). A durable,
/// multi-replica process store is a tracked follow-up; the registry's surface (start → snapshot →
/// stream of updates) is deliberately store-shaped so that lift is localized here.
/// </para>
/// </remarks>
public sealed class ProcessRegistry(TimeProvider clock)
{
    private readonly ConcurrentDictionary<Guid, ProcessEntry> _entries = new();

    /// <summary>
    /// Register a new process in <see cref="ProcessStatus.Processing"/> and return its assigned
    /// <c>process_id</c>. The caller dispatches the command (see <see cref="RunAsync"/>) after the
    /// 202 has been returned, so the SSE client can already be subscribed when the first update lands.
    /// </summary>
    public Guid Start()
    {
        var processId = Guid.NewGuid();
        var entry = new ProcessEntry(ProcessSnapshot.Initial(processId, clock.GetUtcNow()));
        _entries[processId] = entry;
        return processId;
    }

    /// <summary>The current snapshot, or <c>null</c> if no process with that id exists.</summary>
    public ProcessSnapshot? Snapshot(Guid processId) =>
        _entries.TryGetValue(processId, out var entry) ? entry.Current : null;

    /// <summary>
    /// Run a started process's <paramref name="dispatch"/> to a terminal state, publishing each
    /// lifecycle change to subscribers. The delegate IS the engine command path (it calls the
    /// family decider); its returned <c>commit_sequence</c> becomes the read-your-writes token on the
    /// terminal <see cref="ProcessStatus.Succeeded"/> snapshot. A <see cref="DomainRejectedException"/>
    /// is a terminal <see cref="ProcessStatus.Rejected"/> (a domain precondition said no); any other
    /// fault is a terminal <see cref="ProcessStatus.Failed"/> — neither throws out of this method, so a
    /// background dispatch never crashes the host.
    /// </summary>
    public async Task RunAsync(Guid processId, Func<Task<ProcessOutcome>> dispatch)
    {
        if (!_entries.TryGetValue(processId, out var entry))
        {
            return;
        }

        try
        {
            var outcome = await dispatch().ConfigureAwait(false);
            Publish(entry, entry.Current.Succeeded(outcome, clock.GetUtcNow()));
        }
        catch (DomainRejectedException e)
        {
            Publish(entry, entry.Current.Rejected(e.Message, clock.GetUtcNow()));
        }
        catch (Exception e)
        {
            // A wiring/infra fault (a mis-pinned pack, a dropped connection) — terminal FAILED, surfaced
            // to the stream, never swallowed and never allowed to escape the background task.
            Publish(entry, entry.Current.Failed(e.Message, clock.GetUtcNow()));
        }
    }

    /// <summary>
    /// Subscribe to a process's lifecycle. Yields the current snapshot immediately (so a late
    /// subscriber still sees where the process is) then every subsequent update, completing once a
    /// terminal snapshot has been yielded. Returns an empty sequence for an unknown process id.
    /// </summary>
    public async IAsyncEnumerable<ProcessSnapshot> SubscribeAsync(
        Guid processId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!_entries.TryGetValue(processId, out var entry))
        {
            yield break;
        }

        var reader = entry.Subscribe(out var current);

        // Replay the snapshot captured at subscription time first. If it is already terminal the
        // process finished before we subscribed — emit it and stop, never block on a channel that
        // will receive nothing more.
        yield return current;
        if (current.IsTerminal)
        {
            yield break;
        }

        await foreach (var snapshot in reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            yield return snapshot;
            if (snapshot.IsTerminal)
            {
                yield break;
            }
        }
    }

    private static void Publish(ProcessEntry entry, ProcessSnapshot snapshot) => entry.Publish(snapshot);

    /// <summary>
    /// One tracked process: its latest snapshot plus the live subscriber fan-out. Each subscriber gets
    /// its own unbounded channel seeded with the snapshot current at subscription time, so a subscriber
    /// that joins mid-flight never misses the terminal update. Channels are completed on the terminal
    /// publish, ending every <see cref="SubscribeAsync"/> loop.
    /// </summary>
    private sealed class ProcessEntry(ProcessSnapshot initial)
    {
        private readonly Lock _gate = new();
        private readonly List<Channel<ProcessSnapshot>> _subscribers = [];
        private ProcessSnapshot _current = initial;

        public ProcessSnapshot Current
        {
            get
            {
                lock (_gate)
                {
                    return _current;
                }
            }
        }

        public ChannelReader<ProcessSnapshot> Subscribe(out ProcessSnapshot snapshotAtSubscription)
        {
            var channel = Channel.CreateUnbounded<ProcessSnapshot>(
                new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
            lock (_gate)
            {
                snapshotAtSubscription = _current;
                if (_current.IsTerminal)
                {
                    // Already done: hand back a completed channel so the subscriber only sees the replay.
                    channel.Writer.Complete();
                }
                else
                {
                    _subscribers.Add(channel);
                }
            }

            return channel.Reader;
        }

        public void Publish(ProcessSnapshot snapshot)
        {
            lock (_gate)
            {
                _current = snapshot;
                foreach (var subscriber in _subscribers)
                {
                    subscriber.Writer.TryWrite(snapshot);
                    if (snapshot.IsTerminal)
                    {
                        subscriber.Writer.TryComplete();
                    }
                }

                if (snapshot.IsTerminal)
                {
                    _subscribers.Clear();
                }
            }
        }
    }
}

/// <summary>The result of a successful command dispatch: the affected aggregate id and the
/// per-stream <c>commit_sequence</c> the append reached (ADR-IC-005 §P3 read-your-writes token).</summary>
public readonly record struct ProcessOutcome(Guid AggregateId, long CommitSequence);
