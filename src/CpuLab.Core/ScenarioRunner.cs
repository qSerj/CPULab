namespace CpuLab.Core;

public sealed record RunResult(string Directory, string Status, string? Reason);

public sealed class ScenarioRunner(IInventory inventory, ITelemetryProvider telemetry, IBenchmarkAdapter benchmark)
{
    public async Task<RunResult> RunAsync(Scenario scenario, string outputRoot, bool realtime = false,
        LegacyDocument? legacy = null, CancellationToken cancellationToken = default,
        Action<TelemetryFrame>? onFrame = null, Action<string>? onState = null)
    {
        scenario.Validate();
        if (!benchmark.IsSimulated || telemetry.Sensors.Any(s => s.Provider != "mock"))
            throw new InvalidOperationException("This runner supports simulation only; real adapters are not enabled.");
        var clock = new SimulationClock(scenario.SyntheticStartUtc, realtime);
        var metadata = new RunMetadata
        {
            SessionId = Guid.NewGuid().ToString("N"), Mode = "mock", CreatedUtc = DateTimeOffset.UtcNow,
            TimeMode = clock.Mode, Inventory = inventory.Capture(), Profile = MockInventory.Profile
        };
        using var store = RunStore.Create(outputRoot, metadata, scenario, telemetry.Sensors.ToArray(), legacy);
        var attempts = new List<Attempt>();
        int sequence = 0;
        Attempt? current = null;

        void State(string state, string? reason = null)
        {
            metadata.Status = state;
            metadata.Reason = reason;
            store.Event(clock.UtcNow, state, current?.Id, reason);
            // A terminal manifest is committed only after its final attempt snapshot.
            // Otherwise a crash in finalization could leave a "cancelled" session with a running attempt.
            if (state is not ("completed" or "failed" or "cancelled" or "timeout")) store.SaveMetadata(metadata);
            onState?.Invoke(state);
        }

        async Task Capture(string phase)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long remaining = TimeSpan.FromMilliseconds(scenario.TotalTimeoutMs).Ticks - clock.Ticks;
            if (remaining < TimeSpan.FromMilliseconds(scenario.SampleIntervalMs).Ticks)
                throw new TimeoutException("Total experiment timeout reached.");
            var start = clock.UtcNow;
            await clock.AdvanceAsync(scenario.SampleIntervalMs, cancellationToken);
            var request = new SampleRequest(++sequence, phase, current?.Id, current?.Ordinal ?? 0,
                start, clock.UtcNow, clock.Ticks);
            var frame = await telemetry.ReadAsync(request, cancellationToken);
            store.Append(frame); // Save evidence even when the following health check rejects it.
            if (current is not null)
            {
                current.LastObservedUtc = clock.UtcNow;
                store.SaveAttempts(attempts);
            }
            onFrame?.Invoke(frame);
            var temperature = telemetry.Sensors.Where(s => s.Metric == "temperature").Select(s => s.Id).ToHashSet();
            var readings = frame.Samples.Where(s => temperature.Contains(s.SensorId)).ToArray();
            if (readings.Length == 0 || readings.Any(s => s.Validity != "valid" || s.Value is null || !double.IsFinite(s.Value.Value)))
                throw new InvalidDataException("Temperature telemetry unavailable or stale; series stopped.");
            if (readings.Any(s => s.Value >= scenario.TemperatureLimitC))
                throw new InvalidDataException("Configured mock temperature threshold reached; series stopped.");
        }

        try
        {
            State("preflight");
            cancellationToken.ThrowIfCancellationRequested();
            State("baseline");
            for (int i = 0; i < scenario.BaselineSamples; i++) await Capture("baseline");
            for (int ordinal = 1; ordinal <= scenario.Repeat; ordinal++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                current = new Attempt
                {
                    Id = $"attempt-{ordinal:D3}", Ordinal = ordinal, StartUtc = clock.UtcNow, StartTicks = clock.Ticks
                };
                attempts.Add(current);
                store.BeginAttempt(current);
                store.SaveAttempts(attempts); // Intent survives a crash before the first observation.
                State("running");
                await benchmark.StartAsync(current, cancellationToken);
                int collected = 0;
                while (true)
                {
                    long elapsed = clock.Ticks - current.StartTicks;
                    if (elapsed + TimeSpan.FromMilliseconds(scenario.SampleIntervalMs).Ticks > TimeSpan.FromMilliseconds(scenario.AttemptTimeoutMs).Ticks)
                        throw new TimeoutException("Attempt timeout reached.");
                    await Capture("running");
                    var observation = await benchmark.ObserveAsync(current, ++collected, cancellationToken);
                    if (observation.Output is { } output) store.AppendOutput(current, output, observation.Failed);
                    if (!observation.Finished) continue;
                    if (observation.Failed) throw new InvalidDataException(observation.Reason ?? "Adapter failed.");
                    if (observation.Score is not { } score || !double.IsFinite(score) || score <= 0)
                        throw new InvalidDataException("Benchmark completed without one valid score.");
                    current.Score = score;
                    current.Status = "completed";
                    current.EndUtc = clock.UtcNow;
                    current.EndTicks = clock.Ticks;
                    await benchmark.StopAsync(current);
                    store.SaveAttempts(attempts);
                    store.Event(clock.UtcNow, "attempt_completed", current.Id);
                    current = null;
                    break;
                }
                if (ordinal < scenario.Repeat)
                {
                    State("cooldown");
                    for (int i = 0; i < scenario.CooldownSamples; i++) await Capture("cooldown");
                }
            }
            State("completed");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            State("cancelled", "Cancelled by operator; partial data retained.");
        }
        catch (TimeoutException ex) { State("timeout", ex.Message); }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
        {
            State("failed", ex.Message);
        }
        finally
        {
            if (current is not null)
            {
                try { await benchmark.StopAsync(current); }
                catch (Exception ex) { store.Event(clock.UtcNow, "stop_error", current.Id, ex.Message); }
                current.Status = metadata.Status;
                current.Reason = metadata.Reason;
                current.Score = null;
                current.EndUtc = clock.UtcNow;
                current.EndTicks = clock.Ticks;
                store.AppendOutput(current, $"{current.Status}: {current.Reason}\n", true);
            }
            store.SaveAttempts(attempts);
            metadata.FinalizedUtc = DateTimeOffset.UtcNow;
            store.SaveMetadata(metadata);
        }
        return new RunResult(store.DirectoryPath, metadata.Status, metadata.Reason);
    }
}
