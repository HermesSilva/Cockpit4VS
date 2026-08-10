using System.Linq;
using Tootega.Cockpit.Cli;
using Tootega.Cockpit.Protocol;
using Xunit;

namespace Tootega.Cockpit.Tests
{
    /// <summary>
    /// The parser sits between a process we do not control and a UI that must not break.
    /// These tests pin the two properties that matter: events survive arbitrary chunk
    /// boundaries, and anything unparseable is dropped rather than raised.
    /// </summary>
    public class StreamParserTests
    {
        [Fact]
        public void SplitsMultipleEventsInOneChunk()
        {
            var parser = new StreamParser();

            var events = parser.Push("{\"type\":\"system\",\"subtype\":\"init\"}\n{\"type\":\"result\"}\n");

            Assert.Equal(2, events.Count);
            Assert.Equal("system", events[0].Type);
            Assert.Equal("init", events[0].Subtype);
            Assert.Equal("result", events[1].Type);
        }

        [Fact]
        public void HoldsPartialLineUntilNewlineArrives()
        {
            var parser = new StreamParser();

            Assert.Empty(parser.Push("{\"type\":\"assist"));
            Assert.Empty(parser.Push("ant\""));

            var events = parser.Push("}\n");

            Assert.Single(events);
            Assert.Equal("assistant", events[0].Type);
        }

        [Fact]
        public void SurvivesByteLevelChunking()
        {
            // Real stdout splits wherever the pipe buffer ends, including mid-token.
            const string line = "{\"type\":\"stream_event\",\"session_id\":\"abc\"}\n";
            var parser = new StreamParser();
            var collected = 0;

            foreach (var ch in line)
            {
                collected += parser.Push(ch.ToString()).Count;
            }

            Assert.Equal(1, collected);
        }

        [Fact]
        public void DropsUnparseableLines()
        {
            // The CLI sometimes writes plain log noise to stdout. A stray line must not
            // take down a conversation in progress.
            var parser = new StreamParser();

            var events = parser.Push("not json at all\n{\"type\":\"result\"}\n");

            Assert.Single(events);
            Assert.Equal("result", events[0].Type);
        }

        [Fact]
        public void DropsObjectsWithoutStringType()
        {
            var parser = new StreamParser();

            var events = parser.Push("{\"no\":\"type\"}\n[1,2,3]\n\"bare string\"\n{\"type\":42}\n");

            Assert.Empty(events);
        }

        [Fact]
        public void SkipsBlankLines()
        {
            var parser = new StreamParser();

            var events = parser.Push("\n\n   \n{\"type\":\"user\"}\n");

            Assert.Single(events);
        }

        [Fact]
        public void ToleratesCarriageReturns()
        {
            // A Windows pipe can deliver CRLF; the CR must not end up inside the JSON.
            var parser = new StreamParser();

            var events = parser.Push("{\"type\":\"result\"}\r\n");

            Assert.Single(events);
            Assert.Equal("result", events[0].Type);
        }

        [Fact]
        public void FlushEmitsTrailingLineWithoutNewline()
        {
            // A process can die having written a complete event but no final newline.
            var parser = new StreamParser();
            parser.Push("{\"type\":\"result\",\"subtype\":\"success\"}");

            var flushed = parser.Flush();

            Assert.Single(flushed);
            Assert.Equal("success", flushed[0].Subtype);
        }

        [Fact]
        public void FlushOnEmptyBufferReturnsNothing()
        {
            var parser = new StreamParser();

            Assert.Empty(parser.Flush());
        }

        [Fact]
        public void FlushDiscardsPartialJson()
        {
            var parser = new StreamParser();
            parser.Push("{\"type\":\"resu");

            Assert.Empty(parser.Flush());
        }

        [Fact]
        public void KeepsUnknownFieldsAndUnknownTypes()
        {
            // Version tolerance: a type we have never seen still round-trips, and its extra
            // keys stay reachable instead of being lost.
            var parser = new StreamParser();

            var events = parser.Push("{\"type\":\"brand_new_event\",\"whatever\":{\"n\":1}}\n");

            Assert.Single(events);
            Assert.Equal("brand_new_event", events[0].Type);
            Assert.True(events[0].Extra.ContainsKey("whatever"));
        }

        [Fact]
        public void ParsesUsageAndDenialsOnResult()
        {
            var parser = new StreamParser();

            var events = parser.Push(
                "{\"type\":\"result\",\"subtype\":\"success\",\"total_cost_usd\":0.42," +
                "\"usage\":{\"input_tokens\":10,\"output_tokens\":20,\"cache_read_input_tokens\":30}," +
                "\"permission_denials\":[{\"tool_name\":\"Bash\",\"tool_use_id\":\"t1\"}]}\n");

            var result = events.Single();
            Assert.Equal(0.42, result.TotalCostUsd);
            Assert.Equal(10, result.Usage.InputTokens);
            Assert.Equal(20, result.Usage.OutputTokens);
            Assert.Equal(30, result.Usage.CacheReadInputTokens);
            Assert.Equal("Bash", result.PermissionDenials.Single().ToolName);
        }

        [Fact]
        public void ParsesAssistantToolUse()
        {
            var parser = new StreamParser();

            var events = parser.Push(
                "{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"model\":\"claude-opus-5\"," +
                "\"content\":[{\"type\":\"text\",\"text\":\"hi\"}," +
                "{\"type\":\"tool_use\",\"id\":\"tu1\",\"name\":\"Read\",\"input\":{\"file_path\":\"a.cs\"}}]}}\n");

            var message = events.Single().AsAssistantMessage();

            Assert.Equal("claude-opus-5", message.Model);
            Assert.Equal("hi", message.Content[0].Text);
            Assert.Equal("tool_use", message.Content[1].Type);
            Assert.Equal("Read", message.Content[1].Name);
            Assert.Equal("a.cs", message.Content[1].Input.Value.GetProperty("file_path").GetString());
        }

        [Fact]
        public void ParsesControlRequest()
        {
            var parser = new StreamParser();

            var events = parser.Push(
                "{\"type\":\"control_request\",\"request_id\":\"r1\"," +
                "\"request\":{\"subtype\":\"can_use_tool\",\"tool_name\":\"Bash\",\"input\":{\"command\":\"ls\"}}}\n");

            var request = events.Single();
            Assert.Equal("r1", request.RequestId);
            Assert.Equal("can_use_tool", request.Request.Subtype);
            Assert.Equal("Bash", request.Request.ToolName);
        }

        [Fact]
        public void ParsesRateLimitEventWithoutUtilization()
        {
            // At low usage the CLI sends status/resetsAt only; utilization is absent and
            // must stay absent rather than defaulting to zero.
            var parser = new StreamParser();

            var events = parser.Push(
                "{\"type\":\"rate_limit_event\",\"rate_limit_info\":" +
                "{\"status\":\"allowed\",\"rateLimitType\":\"five_hour\",\"resetsAt\":1786000000}}\n");

            var info = events.Single().RateLimitInfo;
            Assert.Equal("allowed", info.Status);
            Assert.Equal(RateLimitBuckets.FiveHour, info.RateLimitType);
            Assert.Null(info.Utilization);
        }
    }
}
