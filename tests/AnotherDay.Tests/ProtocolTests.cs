using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AnotherDay.Shared;
using Xunit;

namespace AnotherDay.Tests
{
    public class ProtocolTests
    {
        [Fact]
        public async Task EnvelopeRoundTripsThroughFrameCodec()
        {
            var envelope = new MessageEnvelope
            {
                Type = MessageTypes.Converse,
                CorrelationId = "abc123",
                PayloadJson = "{\"text\":\"hello \\\"there\\\"\"}"
            };

            using var stream = new MemoryStream();
            await FrameCodec.WriteFrameAsync(stream, envelope, CancellationToken.None);
            stream.Position = 0;
            MessageEnvelope read = await FrameCodec.ReadFrameAsync(stream, CancellationToken.None);

            Assert.NotNull(read);
            Assert.Equal(MessageTypes.Converse, read.Type);
            Assert.Equal("abc123", read.CorrelationId);
            Assert.Equal(envelope.MessageId, read.MessageId);
            Assert.Equal(Protocol.SchemaVersion, read.SchemaVersion);
            Assert.Contains("hello", read.PayloadJson);
        }

        [Fact]
        public async Task UnsupportedSchemaVersionIsRejected()
        {
            var envelope = new MessageEnvelope { Type = MessageTypes.Ping, SchemaVersion = Protocol.SchemaVersion + 1 };
            using var stream = new MemoryStream();
            await FrameCodec.WriteFrameAsync(stream, envelope, CancellationToken.None);
            stream.Position = 0;

            await Assert.ThrowsAsync<InvalidDataException>(() => FrameCodec.ReadFrameAsync(stream, CancellationToken.None));
        }

        [Fact]
        public async Task OversizedFrameIsRejected()
        {
            var envelope = new MessageEnvelope
            {
                Type = MessageTypes.Ping,
                PayloadJson = new string('x', Protocol.MaxFrameBytes + 16)
            };
            using var stream = new MemoryStream();
            await Assert.ThrowsAsync<InvalidOperationException>(() => FrameCodec.WriteFrameAsync(stream, envelope, CancellationToken.None));
        }
    }
}
