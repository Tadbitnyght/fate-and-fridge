using System.Text.Json;
using FateAndFridge.Api.Errors;
using FateAndFridge.Api.Middleware;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// Review fix: the last-resort exception handler must not turn client
/// disconnects into logged 500s, and real failures must still use the shared
/// envelope without leaking details.
/// </summary>
public sealed class ExceptionHandlingMiddlewareTests
{
    private static async Task<string> ReadBodyAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        return await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Aborted_request_cancellations_are_rethrown_and_not_logged_as_500()
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
        };
        context.Response.Body = new MemoryStream();
        context.RequestAborted = new CancellationToken(canceled: true);
        var logger = new ListLogger();
        var middleware = new ExceptionHandlingMiddleware(
            _ => throw new OperationCanceledException("client went away"),
            logger);

        var act = async () => await middleware.InvokeAsync(context);

        await act.Should().ThrowAsync<OperationCanceledException>();
        logger.Levels.Should().BeEmpty();
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK); // untouched
    }

    [Fact]
    public async Task Unexpected_exceptions_become_the_shared_envelope_and_are_logged()
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
        };
        context.Response.Body = new MemoryStream();
        var logger = new ListLogger();
        var middleware = new ExceptionHandlingMiddleware(
            _ => throw new InvalidOperationException("boom: secret detail"),
            logger);

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        var raw = await ReadBodyAsync(context);
        var error = JsonDocument.Parse(raw).RootElement.GetProperty("error");
        error.GetProperty("code").GetString().Should().Be(ErrorCodes.InternalError);
        raw.Should().NotContain("boom: secret detail");
        raw.Should().NotContain("InvalidOperationException");
        logger.Levels.Should().Equal(LogLevel.Error);
    }

    [Fact]
    public async Task Non_cancellation_failures_on_aborted_requests_still_use_the_envelope()
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
        };
        context.Response.Body = new MemoryStream();
        context.RequestAborted = new CancellationToken(canceled: true);
        var logger = new ListLogger();
        var middleware = new ExceptionHandlingMiddleware(
            _ => throw new InvalidOperationException("still a server bug"),
            logger);

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        logger.Levels.Should().Equal(LogLevel.Error);
    }

    private sealed class ListLogger : ILogger<ExceptionHandlingMiddleware>
    {
        public List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => Levels.Add(logLevel);
    }
}
