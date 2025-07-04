using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Internal;

namespace Orleans.Runtime.Messaging
{
    internal abstract partial class ConnectionListener
    {
        private readonly IConnectionListenerFactory listenerFactory;
        private readonly ConnectionManager connectionManager;
        protected readonly ConcurrentDictionary<Connection, object> connections = new(ReferenceEqualsComparer.Default);
        private readonly ConnectionCommon connectionShared;
        private Task acceptLoopTask;
        private IConnectionListener listener;
        private ConnectionDelegate connectionDelegate;

        protected ConnectionListener(
            IConnectionListenerFactory listenerFactory,
            IOptions<ConnectionOptions> connectionOptions,
            ConnectionManager connectionManager,
            ConnectionCommon connectionShared)
        {
            this.listenerFactory = listenerFactory;
            this.connectionManager = connectionManager;
            this.ConnectionOptions = connectionOptions.Value;
            this.connectionShared = connectionShared;
        }

        public abstract EndPoint Endpoint { get; }

        protected IServiceProvider ServiceProvider => this.connectionShared.ServiceProvider;

        protected NetworkingTrace NetworkingTrace => this.connectionShared.NetworkingTrace;

        protected ConnectionOptions ConnectionOptions { get; }

        protected abstract Connection CreateConnection(ConnectionContext context);

        protected ConnectionDelegate ConnectionDelegate
        {
            get
            {
                if (this.connectionDelegate != null) return this.connectionDelegate;

                lock (this)
                {
                    if (this.connectionDelegate != null) return this.connectionDelegate;

                    // Configure the connection builder using the user-defined options.
                    var connectionBuilder = new ConnectionBuilder(this.ServiceProvider);
                    connectionBuilder.Use(next =>
                    {
                        return context =>
                        {
                            context.Features.Set<IUnderlyingTransportFeature>(new UnderlyingConnectionTransportFeature { Transport = context.Transport });
                            return next(context);
                        };
                    });
                    this.ConfigureConnectionBuilder(connectionBuilder);
                    Connection.ConfigureBuilder(connectionBuilder);
                    return this.connectionDelegate = connectionBuilder.Build();
                }
            }
        }

        protected virtual void ConfigureConnectionBuilder(IConnectionBuilder connectionBuilder) { }

        protected async Task BindAsync()
        {
            this.listener = await this.listenerFactory.BindAsync(this.Endpoint);
        }

        protected void Start()
        {
            if (this.listener is null) throw new InvalidOperationException("Listener is not bound");
            acceptLoopTask = RunAcceptLoop();
        }

        private async Task RunAcceptLoop()
        {
            await Task.Yield();
            try
            {
                while (true)
                {
                    var context = await this.listener.AcceptAsync();
                    if (context == null) break;

                    var connection = this.CreateConnection(context);
                    this.StartConnection(connection);
                }
            }
            catch (Exception exception)
            {
                LogCriticalExceptionInAcceptAsync(this.NetworkingTrace, exception);
            }
        }

        protected async Task StopAsync(CancellationToken cancellationToken)
        {
            try
            {
                await listener.UnbindAsync(cancellationToken);

                if (acceptLoopTask is not null)
                {
                    await acceptLoopTask;
                }

                var closeTasks = new List<Task>();
                foreach (var kv in connections)
                {
                    closeTasks.Add(kv.Key.CloseAsync(exception: null));
                }

                if (closeTasks.Count > 0)
                {
                    await Task.WhenAll(closeTasks).WaitAsync(cancellationToken).SuppressThrowing();
                }

                await this.connectionManager.Closed;
                await this.listener.DisposeAsync();
            }
            catch (Exception exception)
            {
                LogWarningExceptionDuringShutdown(this.NetworkingTrace, exception);
            }
        }

        private void StartConnection(Connection connection)
        {
            connections.TryAdd(connection, null);

            ThreadPool.UnsafeQueueUserWorkItem(state =>
            {
                var (t, connection) = ((ConnectionListener, Connection))state;
                t.RunConnectionAsync(connection).Ignore();
            }, (this, connection));
        }

        private async Task RunConnectionAsync(Connection connection)
        {
            using (this.BeginConnectionScope(connection))
            {
                try
                {
                    await connection.Run();
                    LogInformationConnectionTerminated(this.NetworkingTrace, connection);
                }
                catch (Exception exception)
                {
                    LogInformationConnectionTerminatedWithException(this.NetworkingTrace, exception, connection);
                }
                finally
                {
                    this.connections.TryRemove(connection, out _);
                }
            }
        }

        private IDisposable BeginConnectionScope(Connection connection)
        {
            if (this.NetworkingTrace.IsEnabled(LogLevel.Critical))
            {
                return this.NetworkingTrace.BeginScope(new ConnectionLogScope(connection));
            }

            return null;
        }

        [LoggerMessage(
            Level = LogLevel.Critical,
            Message = "Exception in AcceptAsync"
        )]
        private static partial void LogCriticalExceptionInAcceptAsync(ILogger logger, Exception exception);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Exception during shutdown"
        )]
        private static partial void LogWarningExceptionDuringShutdown(ILogger logger, Exception exception);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Connection {Connection} terminated"
        )]
        private static partial void LogInformationConnectionTerminated(ILogger logger, Connection connection);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Connection {Connection} terminated with an exception"
        )]
        private static partial void LogInformationConnectionTerminatedWithException(ILogger logger, Exception exception, Connection connection);
    }
}