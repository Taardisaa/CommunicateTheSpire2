using System;
using CommunicateTheSpire2.Protocol;
using Xunit;

namespace CommunicateTheSpire2.Tests;

public sealed class ProtocolCommandParserTests
{
	[Fact]
	public void TryParse_null_line_returns_false_and_error()
	{
		bool ok = ProtocolCommandParser.TryParse(null!, out string cmd, out string? reqId, out string? args, out ErrorMessage? err);
		Assert.False(ok);
		Assert.NotNull(err);
		Assert.Equal("InvalidCommand", err!.error);
		Assert.NotNull(err.details);
	}

	[Fact]
	public void TryParse_empty_line_returns_false_and_error()
	{
		bool ok = ProtocolCommandParser.TryParse("", out string cmd, out string? reqId, out string? args, out ErrorMessage? err);
		Assert.False(ok);
		Assert.NotNull(err);
		Assert.Equal("InvalidCommand", err!.error);
		Assert.Contains("empty", err.details!, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void TryParse_whitespace_only_returns_false_and_error()
	{
		bool ok = ProtocolCommandParser.TryParse("   \t\n  ", out string cmd, out string? reqId, out string? args, out ErrorMessage? err);
		Assert.False(ok);
		Assert.NotNull(err);
	}

	[Fact]
	public void TryParse_plain_STATE_returns_true_no_args()
	{
		bool ok = ProtocolCommandParser.TryParse("STATE", out string cmd, out string? reqId, out string? args, out ErrorMessage? err);
		Assert.True(ok);
		Assert.Null(err);
		Assert.Equal("STATE", cmd);
		Assert.Null(args);
	}

	[Fact]
	public void TryParse_plain_PLAY_with_args()
	{
		bool ok = ProtocolCommandParser.TryParse("PLAY 0 1", out string cmd, out string? reqId, out string? args, out ErrorMessage? err);
		Assert.True(ok);
		Assert.Null(err);
		Assert.Equal("PLAY", cmd);
		Assert.Equal("0 1", args);
	}

	[Fact]
	public void TryParse_plain_END_returns_true()
	{
		bool ok = ProtocolCommandParser.TryParse("END", out string cmd, out string? reqId, out string? args, out ErrorMessage? err);
		Assert.True(ok);
		Assert.Equal("END", cmd);
		Assert.Null(args);
	}

	[Fact]
	public void TryParse_unknown_plain_command_returns_true()
	{
		bool ok = ProtocolCommandParser.TryParse("NOTACOMMAND foo bar", out string cmd, out string? reqId, out string? args, out ErrorMessage? err);
		Assert.True(ok);
		Assert.Null(err);
		Assert.Equal("NOTACOMMAND", cmd);
		Assert.Equal("foo bar", args);
	}

	[Fact]
	public void TryParse_JSON_command_valid_returns_true()
	{
		string line = """{"type":"command","command":"PLAY","args":"0 1"}""";
		bool ok = ProtocolCommandParser.TryParse(line, out string cmd, out string? reqId, out string? args, out ErrorMessage? err);
		Assert.True(ok);
		Assert.Null(err);
		Assert.Equal("PLAY", cmd);
		Assert.Equal("0 1", args);
	}

	[Fact]
	public void TryParse_JSON_with_request_id()
	{
		string line = """{"type":"command","command":"STATE","request_id":"abc-123"}""";
		bool ok = ProtocolCommandParser.TryParse(line, out string cmd, out string? reqId, out string? args, out ErrorMessage? err);
		Assert.True(ok);
		Assert.Null(err);
		Assert.Equal("STATE", cmd);
		Assert.Equal("abc-123", reqId);
	}

	[Fact]
	public void TryParse_JSON_missing_type_command_returns_false()
	{
		string line = """{"type":"hello"}""";
		bool ok = ProtocolCommandParser.TryParse(line, out string cmd, out string? reqId, out string? args, out ErrorMessage? err);
		Assert.False(ok);
		Assert.NotNull(err);
		Assert.Equal("InvalidCommand", err!.error);
		Assert.Contains("command", err.details!, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void TryParse_JSON_missing_command_field_returns_false()
	{
		string line = """{"type":"command"}""";
		bool ok = ProtocolCommandParser.TryParse(line, out string cmd, out string? reqId, out string? args, out ErrorMessage? err);
		Assert.False(ok);
		Assert.NotNull(err);
	}

	[Fact]
	public void TryParse_JSON_invalid_syntax_returns_false()
	{
		string line = """{"type":"command","command":"PLAY""";
		bool ok = ProtocolCommandParser.TryParse(line, out string cmd, out string? reqId, out string? args, out ErrorMessage? err);
		Assert.False(ok);
		Assert.NotNull(err);
		Assert.Equal("InvalidJson", err!.error);
	}
}
