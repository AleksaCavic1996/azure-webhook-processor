// Moves messages from a queue's dead-letter queue back to the queue, so the worker tries them again.
//
// Use it after the cause of the failures is fixed (for example a database outage that outlasted
// the retries). Replaying is safe to repeat: processing is idempotent, so an event that was
// already applied is recorded as a duplicate.
//
//   dotnet run --project tools/DeadLetterReplay -- --namespace <name>.servicebus.windows.net --dry-run
//   dotnet run --project tools/DeadLetterReplay -- --namespace <name>.servicebus.windows.net
//
// On Azure it signs in as the current user (az login), who needs the
// "Azure Service Bus Data Owner" role on the queue. For the local emulator pass --connection-string.

using Azure.Identity;
using Azure.Messaging.ServiceBus;

string? fullyQualifiedNamespace = null;
string? connectionString = null;
var queueName = "payment-events";
var max = 100;
var dryRun = false;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--namespace": fullyQualifiedNamespace = RequireValue(args, ref i); break;
        case "--connection-string": connectionString = RequireValue(args, ref i); break;
        case "--queue": queueName = RequireValue(args, ref i); break;
        case "--max": max = int.Parse(RequireValue(args, ref i)); break;
        case "--dry-run": dryRun = true; break;
        default:
            Console.Error.WriteLine($"Unknown argument: {args[i]}");
            return 2;
    }
}

if (fullyQualifiedNamespace is null == connectionString is null)
{
    Console.Error.WriteLine("Pass exactly one of --namespace or --connection-string.");
    return 2;
}

await using var client = connectionString is not null
    ? new ServiceBusClient(connectionString)
    : new ServiceBusClient(fullyQualifiedNamespace, new DefaultAzureCredential());

await using var deadLetters = client.CreateReceiver(
    queueName,
    new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });

if (dryRun)
{
    // Peeking takes no lock and changes nothing.
    var peeked = await deadLetters.PeekMessagesAsync(max);
    foreach (var message in peeked)
    {
        Console.WriteLine(Describe(message));
    }

    Console.WriteLine($"{peeked.Count} message(s) in the dead-letter queue of {queueName}. Nothing was changed.");
    return 0;
}

await using var sender = client.CreateSender(queueName);
var replayed = 0;

while (replayed < max)
{
    var message = await deadLetters.ReceiveMessageAsync(TimeSpan.FromSeconds(5));
    if (message is null)
    {
        break;
    }

    // Send first, then remove from the dead-letter queue. If the tool stops in between, the
    // message is still dead-lettered and a rerun sends it again; the worker handles the duplicate.
    // The copy starts with a delivery count of zero, so it gets the full set of retries.
    await sender.SendMessageAsync(new ServiceBusMessage(message));
    await deadLetters.CompleteMessageAsync(message);

    replayed++;
    Console.WriteLine($"replayed  {Describe(message)}");
}

Console.WriteLine($"{replayed} message(s) moved back to {queueName}.");
return 0;

static string RequireValue(string[] args, ref int index)
{
    if (index + 1 >= args.Length)
    {
        throw new ArgumentException($"{args[index]} needs a value.");
    }

    return args[++index];
}

static string Describe(ServiceBusReceivedMessage message) =>
    $"{message.MessageId}  dead-lettered: {message.DeadLetterReason ?? "unknown"}" +
    (string.IsNullOrEmpty(message.DeadLetterErrorDescription) ? "" : $" ({message.DeadLetterErrorDescription})");
