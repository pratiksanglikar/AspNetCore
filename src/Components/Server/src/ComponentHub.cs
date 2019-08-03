// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Microsoft.AspNetCore.Components.Server
{
    // Some notes about our expectations for error handling:
    //
    // In general, we need to prevent any client from interacting with a circuit that's in an unpredictable
    // state. This means that when a circuit throws an unhandled exception our top priority is to
    // unregister and dispose the circuit. This will prevent any new dispatches from the client
    // from making it into application code.
    //
    // As part of this process, we also notify the client (if there is one) of the error, and we
    // *expect* a well-behaved client to disconnect. A malicious client can't be expected to disconnect,
    // but since we've unregistered the circuit they won't be able to access it anyway. When a call
    // comes into any hub method and the circuit has been disassociated, we will abort the connection.
    // It's safe to assume that's the result of a race condition or misbehaving client.
    //
    // Now it's important to remember that we can only abort a connection as part of a hub method call.
    // We can dispose a circuit in the background, but we have to deal with a possible race condition
    // any time we try to acquire access to the circuit - because it could have gone away in the
    // background - outside of the scope of a hub method.
    //
    // In general we author our Hub methods as async methods, but we fire-and-forget anything that
    // needs access to the circuit/application state to unblock the message loop. Using async in our
    // Hub methods allows us to ensure message delivery to the client before we abort the connection
    // in error cases.
    internal sealed class ComponentHub : Hub
    {
        private static readonly object CircuitKey = new object();
        private readonly CircuitFactory _circuitFactory;
        private readonly CircuitRegistry _circuitRegistry;
        private readonly CircuitOptions _options;
        private readonly ILogger _logger;

        public ComponentHub(
            CircuitFactory circuitFactory,
            CircuitRegistry circuitRegistry,
            ILogger<ComponentHub> logger,
            IOptions<CircuitOptions> options)
        {
            _circuitFactory = circuitFactory;
            _circuitRegistry = circuitRegistry;
            _options = options.Value;
            _logger = logger;
        }

        /// <summary>
        /// Gets the default endpoint path for incoming connections.
        /// </summary>
        public static PathString DefaultPath { get; } = "/_blazor";

        // We store the CircuitHost through a *handle* here because Context.Items is tied to the lifetime
        // of the connection. It's possible that a misbehaving client could cause disposal of a CircuitHost
        // but keep a connection open indefinitely, preventing GC of the Circuit and related application state.
        // Using a handle allows the CircuitHost to clear this reference in the background.
        //
        // See comment on error handling on the class definition.
        private CircuitHost CircuitHost
        {
            get => ((CircuitHandle)Context.Items[CircuitKey])?.CircuitHost;
            set => Context.Items[CircuitKey] = value?.Handle;
        }

        public override Task OnDisconnectedAsync(Exception exception)
        {
            // If the CircuitHost is gone now this isn't an error. This could happen if the disconnect
            // if the result of well behaving client hanging up after an unhandled exception.
            var circuitHost = CircuitHost;
            if (circuitHost == null)
            {
                return Task.CompletedTask;
            }

            return _circuitRegistry.DisconnectAsync(circuitHost, Context.ConnectionId);
        }

        public async ValueTask<string> StartCircuit(string baseUri, string uri)
        {
            if (CircuitHost != null)
            {
                // This is an error condition and an attempt to bind multiple circuits to a single connection.
                // We can reject this and terminate the connection.
                Log.CircuitAlreadyInitialized(_logger, CircuitHost.CircuitId);
                await NotifyClientError(Clients.Caller, $"The circuit host '{CircuitHost.CircuitId}' has already been initialized.");
                Context.Abort();
                return null;
            }

            if (baseUri == null ||
                uri == null ||
                !Uri.IsWellFormedUriString(baseUri, UriKind.Absolute) ||
                !Uri.IsWellFormedUriString(uri, UriKind.Absolute))
            {
                // We do some really minimal validation here to prevent obviously wrong data from getting in
                // without duplicating too much logic.
                //
                // This is an error condition attempting to initialize the circuit in a way that would fail.
                // We can reject this and terminate the connection.
                Log.InvalidInputData(_logger);
                await NotifyClientError(Clients.Caller, $"The uris provided are invalid.");
                Context.Abort();
                return null;
            }

            // From this point, we can try to actually initialize the circuit.
            if (DefaultCircuitFactory.ResolveComponentMetadata(Context.GetHttpContext() ).Count == 0)
            {
                // No components preregistered so return. This is totally normal if the components were prerendered.
                Log.NoComponentsRegisteredInEndpoint(_logger, Context.GetHttpContext().GetEndpoint()?.DisplayName);
                return null;
            }

            try
            {
                var circuitClient = new CircuitClientProxy(Clients.Caller, Context.ConnectionId);
                var circuitHost = _circuitFactory.CreateCircuitHost(
                    Context.GetHttpContext(),
                    circuitClient,
                    baseUri,
                    uri,
                    Context.User);

                circuitHost.UnhandledException += CircuitHost_UnhandledException;

                // Fire-and-forget the initialization process, because we can't block the
                // SignalR message loop (we'd get a deadlock if any of the initialization
                // logic relied on receiving a subsequent message from SignalR), and it will
                // take care of its own errors anyway.
                _ = circuitHost.InitializeAsync(Context.ConnectionAborted);

                // It's safe to *publish* the circuit now because nothing will be able
                // to run inside it until after InitializeAsync completes.
                _circuitRegistry.Register(circuitHost);
                CircuitHost = circuitHost;
                return circuitHost.CircuitId;
            }
            catch (Exception ex)
            {
                // If the circuit fails to initialize synchronously we can notify the client immediately
                // and shut down the connection.
                Log.CircuitInitializationFailed(_logger, ex);
                await NotifyClientError(Clients.Caller, "The circuit failed to initialize.");
                Context.Abort();
                return null;
            }
        }

        public async ValueTask<bool> ConnectCircuit(string circuitId)
        {
            var circuitHost = await _circuitRegistry.ConnectAsync(circuitId, Clients.Caller, Context.ConnectionId, Context.ConnectionAborted);
            if (circuitHost != null)
            {
                CircuitHost = circuitHost;
                circuitHost.UnhandledException += CircuitHost_UnhandledException;

                circuitHost.SetCircuitUser(Context.User);
                circuitHost.SendPendingBatches();
                return true;
            }

            // If we get here the circuit does not exist anymore. This is something that's valid for a client to
            // recover from, and the client is not holding any resources right now other than the connection.
            return false;
        }

        public async ValueTask BeginInvokeDotNetFromJS(string callId, string assemblyName, string methodIdentifier, long dotNetObjectId, string argsJson)
        {
            var circuitHost = await GetActiveCircuitAsync();
            if (circuitHost == null)
            {
                return;
            }

            _ = CircuitHost.BeginInvokeDotNetFromJS(callId, assemblyName, methodIdentifier, dotNetObjectId, argsJson);
        }

        public async ValueTask EndInvokeJSFromDotNet(long asyncHandle, bool succeeded, string arguments)
        {
            var circuitHost = await GetActiveCircuitAsync();
            if (circuitHost == null)
            {
                return;
            }

            _ = CircuitHost.EndInvokeJSFromDotNet(asyncHandle, succeeded, arguments);
        }

        public async ValueTask DispatchBrowserEvent(string eventDescriptor, string eventArgs)
        {
            var circuitHost = await GetActiveCircuitAsync();
            if (circuitHost == null)
            {
                return;
            }

            _ = CircuitHost.DispatchEvent(eventDescriptor, eventArgs);
        }

        public async ValueTask OnRenderCompleted(long renderId, string errorMessageOrNull)
        {
            var circuitHost = await GetActiveCircuitAsync();
            if (circuitHost == null)
            {
                return;
            }

            Log.ReceivedConfirmationForBatch(_logger, renderId);
            _ = CircuitHost.Renderer.OnRenderCompleted(renderId, errorMessageOrNull);
        }

        public async ValueTask OnLocationChanged(string uri, bool intercepted)
        {
            var circuitHost = await GetActiveCircuitAsync();
            if (circuitHost == null)
            {
                return;
            }

            _ = CircuitHost.OnLocationChangedAsync(uri, intercepted);
        }

        private async ValueTask<CircuitHost> GetActiveCircuitAsync([CallerMemberName] string callSite = "")
        {
            var circuitHost = CircuitHost;
            if (circuitHost == null)
            {
                // This can occur when a circuit host does not exist anymore due to an unhandled exception.
                // We can reject this and terminate the connection.
                Log.CircuitHostNotInitialized(_logger, callSite);
                await NotifyClientError(Clients.Caller, "Circuit not initialized.");
                Context.Abort();
                return null;
            }

            return circuitHost;
        }

        private async void CircuitHost_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var circuitHost = (CircuitHost)sender;
            var circuitId = circuitHost?.CircuitId;

            try
            {
                Log.UnhandledExceptionInCircuit(_logger, circuitId, (Exception)e.ExceptionObject);
                if (_options.DetailedErrors)
                {
                    await NotifyClientError(circuitHost.Client, e.ExceptionObject.ToString());
                }
                else
                {
                    var message = $"There was an unhandled exception on the current circuit, so this circuit will be terminated. For more details turn on " +
                        $"detailed exceptions in '{typeof(CircuitOptions).Name}.{nameof(CircuitOptions.DetailedErrors)}'";

                    await NotifyClientError(circuitHost.Client, message);
                }

                // We generally can't abort the connection here since this is an async
                // callback. The Hub has already been torn down. We'll rely on the
                // client to abort the connection if we successfully transmit an error.
            }
            catch (Exception ex)
            {
                Log.FailedToTransmitException(_logger, circuitId, ex);
            }
        }

        private static Task NotifyClientError(IClientProxy client, string error) =>
            client.SendAsync("JS.Error", error);

        private static class Log
        {
            private static readonly Action<ILogger, string, Exception> _noComponentsRegisteredInEndpoint =
                LoggerMessage.Define<string>(LogLevel.Debug, new EventId(1, "NoComponentsRegisteredInEndpoint"), "No components registered in the current endpoint '{Endpoint}'");

            private static readonly Action<ILogger, long, Exception> _receivedConfirmationForBatch =
                LoggerMessage.Define<long>(LogLevel.Debug, new EventId(2, "ReceivedConfirmationForBatch"), "Received confirmation for batch {BatchId}");

            private static readonly Action<ILogger, string, Exception> _unhandledExceptionInCircuit =
                LoggerMessage.Define<string>(LogLevel.Warning, new EventId(3, "UnhandledExceptionInCircuit"), "Unhandled exception in circuit {CircuitId}");

            private static readonly Action<ILogger, string, Exception> _failedToTransmitException =
                LoggerMessage.Define<string>(LogLevel.Debug, new EventId(4, "FailedToTransmitException"), "Failed to transmit exception to client in circuit {CircuitId}");

            private static readonly Action<ILogger, string, Exception> _circuitAlreadyInitialized =
                LoggerMessage.Define<string>(LogLevel.Debug, new EventId(5, "CircuitAlreadyInitialized"), "The circuit host '{CircuitId}' has already been initialized");

            private static readonly Action<ILogger, string, Exception> _circuitHostNotInitialized =
                LoggerMessage.Define<string>(LogLevel.Debug, new EventId(6, "CircuitHostNotInitialized"), "Call to '{CallSite}' received before the circuit host initialization");

            private static readonly Action<ILogger, string, Exception> _circuitTerminatedGracefully =
                LoggerMessage.Define<string>(LogLevel.Debug, new EventId(7, "CircuitTerminatedGracefully"), "Circuit '{CircuitId}' terminated gracefully");

            private static readonly Action<ILogger, string, Exception> _invalidInputData =
                LoggerMessage.Define<string>(LogLevel.Debug, new EventId(8, "InvalidInputData"), "Call to '{CallSite}' received invalid input data");

            private static readonly Action<ILogger, Exception> _circuitInitializationFailed =
                LoggerMessage.Define(LogLevel.Debug, new EventId(9, "CircuitInitializationFailed"), "Circuit initialization failed");

            public static void NoComponentsRegisteredInEndpoint(ILogger logger, string endpointDisplayName)
            {
                _noComponentsRegisteredInEndpoint(logger, endpointDisplayName, null);
            }

            public static void ReceivedConfirmationForBatch(ILogger logger, long batchId)
            {
                _receivedConfirmationForBatch(logger, batchId, null);
            }

            public static void UnhandledExceptionInCircuit(ILogger logger, string circuitId, Exception exception)
            {
                _unhandledExceptionInCircuit(logger, circuitId, exception);
            }

            public static void FailedToTransmitException(ILogger logger, string circuitId, Exception transmissionException)
            {
                _failedToTransmitException(logger, circuitId, transmissionException);
            }

            public static void CircuitAlreadyInitialized(ILogger logger, string circuitId) => _circuitAlreadyInitialized(logger, circuitId, null);

            public static void CircuitHostNotInitialized(ILogger logger, [CallerMemberName] string callSite = "") => _circuitHostNotInitialized(logger, callSite, null);

            public static void CircuitTerminatedGracefully(ILogger logger, string circuitId) => _circuitTerminatedGracefully(logger, circuitId, null);

            public static void InvalidInputData(ILogger logger, [CallerMemberName] string callSite = "") => _invalidInputData(logger, callSite, null);

            public static void CircuitInitializationFailed(ILogger logger, Exception exception) => _circuitInitializationFailed(logger, exception);
        }
    }
}
