using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace Babelstone.InboxConsumer.Tests;

/// <summary>
/// A single-node Redpanda (dev-container) exposing the Kafka API and the built-in Confluent Schema
/// Registry, pinned to the SAME image as the dev stack (infra/compose.yaml). A copy of the proven
/// OutboxPublisher.Tests fixture — the inbox lane is the consumer mirror, so it reuses the producer
/// lane's exact broker setup (hand-rolled generic container; no first-party Testcontainers.Redpanda).
/// </summary>
public sealed class RedpandaFixture : IAsyncLifetime
{
    private const string Image = "docker.redpanda.com/redpandadata/redpanda:v24.3.1";
    private const int KafkaInternalPort = 9092;
    private const int KafkaExternalPort = 29092;
    private const int SchemaRegistryPort = 8081;

    private IContainer _container = null!;

    public string BootstrapServers { get; private set; } = null!;

    public string SchemaRegistryUrl { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _container = new ContainerBuilder(Image)
            .WithExposedPort(KafkaExternalPort)
            .WithExposedPort(SchemaRegistryPort)
            .WithPortBinding(KafkaExternalPort, assignRandomHostPort: true)
            .WithPortBinding(SchemaRegistryPort, assignRandomHostPort: true)
            .WithEntrypoint("/bin/sh", "-c")
            .WithCommand("while [ ! -f /tmp/redpanda-start.sh ]; do sleep 0.1; done; exec /bin/sh /tmp/redpanda-start.sh")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilMessageIsLogged("Successfully started Redpanda!"))
            .WithStartupCallback(async (container, ct) =>
            {
                var externalHostPort = container.GetMappedPublicPort(KafkaExternalPort);
                var startScript =
                    "#!/bin/sh\nexec rpk redpanda start --mode dev-container --smp 1 --default-log-level=info " +
                    $"--kafka-addr internal://0.0.0.0:{KafkaInternalPort},external://0.0.0.0:{KafkaExternalPort} " +
                    $"--advertise-kafka-addr internal://127.0.0.1:{KafkaInternalPort},external://127.0.0.1:{externalHostPort} " +
                    $"--schema-registry-addr 0.0.0.0:{SchemaRegistryPort}\n";
                var bytes = System.Text.Encoding.UTF8.GetBytes(startScript);
                await container.CopyAsync(bytes, "/tmp/redpanda-start.sh", ct: ct);
            })
            .Build();

        await _container.StartAsync();

        var kafkaPort = _container.GetMappedPublicPort(KafkaExternalPort);
        var srPort = _container.GetMappedPublicPort(SchemaRegistryPort);
        BootstrapServers = $"127.0.0.1:{kafkaPort}";
        SchemaRegistryUrl = $"http://127.0.0.1:{srPort}";

        await WaitForClusterHealthAsync();
        await WaitForSchemaRegistryAsync();
    }

    private async Task WaitForClusterHealthAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            var result = await _container.ExecAsync(["/bin/sh", "-c", "rpk cluster health"]);
            if (result.ExitCode == 0 && result.Stdout.Contains("Healthy:") && result.Stdout.Contains("true"))
            {
                return;
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    $"Redpanda cluster did not become healthy within 60s. Last rpk output:\n{result.Stdout}\n{result.Stderr}");
            }

            await Task.Delay(500);
        }
    }

    private async Task WaitForSchemaRegistryAsync()
    {
        using var http = new HttpClient { BaseAddress = new Uri(SchemaRegistryUrl), Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTime.UtcNow.AddSeconds(60);
        string lastSignal = "no attempt completed";
        while (true)
        {
            try
            {
                using var response = await http.GetAsync("subjects");
                if (response.IsSuccessStatusCode)
                {
                    return;
                }

                lastSignal = $"HTTP {(int)response.StatusCode}";
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                lastSignal = ex.GetType().Name + ": " + ex.Message;
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    $"Redpanda Schema Registry at {SchemaRegistryUrl} did not become ready within 60s. Last signal: {lastSignal}");
            }

            await Task.Delay(500);
        }
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();
}
