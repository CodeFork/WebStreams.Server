﻿// --------------------------------------------------------------------------------------------------------------------
// <copyright file="OwinExtensions.cs" company="Dapr Labs">
//   Copyright 2014.
// </copyright>
// <summary>
//   OWIN extensions.
// </summary>
// --------------------------------------------------------------------------------------------------------------------

namespace Dapr.WebStream.Server
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net.WebSockets;
    using System.Reactive;
    using System.Reactive.Linq;
    using System.Reactive.Subjects;
    using System.Reactive.Threading.Tasks;
    using System.Threading.Tasks;

    using Newtonsoft.Json;
    using Newtonsoft.Json.Serialization;

    using Owin;

    /// <summary>
    /// OWIN extensions.
    /// </summary>
    public static class OwinExtensions
    {
        /// <summary>
        /// Initializes static members of the <see cref="OwinExtensions"/> class.
        /// </summary>
        static OwinExtensions()
        {
            DefaultSerializerSettings = new JsonSerializerSettings
                                        {
                                            ContractResolver = new CamelCasePropertyNamesContractResolver(),
                                            NullValueHandling = NullValueHandling.Ignore,
                                            MissingMemberHandling = MissingMemberHandling.Ignore,
                                        };
        }

        /// <summary>
        /// Gets or sets the default serializer settings.
        /// </summary>
        public static JsonSerializerSettings DefaultSerializerSettings { get; set; }

        /// <summary>
        /// Use the WebStream middleware for the provided controller type.
        /// </summary>
        /// <param name="app">
        /// The app.
        /// </param>
        /// <param name="serializerSettings">
        /// The serializer settings, or <see langword="null"/> to use <see cref="DefaultSerializerSettings"/>.
        /// </param>
        /// <typeparam name="T">
        /// The stream controller type.
        /// </typeparam>
        public static void UseWebStream<T>(this IAppBuilder app, JsonSerializerSettings serializerSettings = null)
        {
            serializerSettings = serializerSettings ?? DefaultSerializerSettings;

            var builder = new StreamControllerManager();
            var routes = builder.GetStreamRoutes(typeof(T));

            var controller = builder.GetStreamController(typeof(T));
            var invokers = routes.ToDictionary(r => r.Route, r => builder.GetInvoker(r.Handler, serializerSettings));
            app.Use(
                async (ctx, next) =>
                {
                    var path = ctx.Request.Path.Value;

                    var accept = ctx.Get<Action<IDictionary<string, object>, Func<IDictionary<string, object>, Task>>>(WebSocketConstants.Accept);
                    if (accept != null)
                    {
                        var route = invokers.FirstOrDefault(r => path.StartsWith(r.Key));
                        if (route.Value != null)
                        {
                            // Accept the socket.
                            var args = ctx.Request.Query.ToDictionary(x => x.Key, x => x.Value.FirstOrDefault());
                            accept(null, CreateStreamHandler(route.Value, controller, args));
                        }
                        else
                        {
                            await next();
                        }
                    }
                    else
                    {
                        await next();
                    }
                });
        }

        /// <summary>
        /// Returns a handler for incoming Web stream requests.
        /// </summary>
        /// <param name="handler">
        /// The handler.
        /// </param>
        /// <param name="controller">
        /// The controller.
        /// </param>
        /// <param name="args">
        /// The request parameters.
        /// </param>
        /// <returns>
        /// A handler for incoming Web stream requests.
        /// </returns>
        private static Func<IDictionary<string, object>, Task> CreateStreamHandler(
            StreamControllerManager.Invoker handler,
            object controller,
            IDictionary<string, string> args)
        {
            return async environment =>
            {
                using (var socket = new WebSocket(environment))
                {
                    var incoming = new Dictionary<string, Subject<string>>();
                    Func<string, IObservable<string>> getIncoming = name =>
                    {
                        Subject<string> result;
                        if (!incoming.TryGetValue(name, out result))
                        {
                            result = new Subject<string>();
                            incoming.Add(name, result);
                        }

                        return result;
                    };

                    // Hook up the incoming and outgoing message pumps.
                    var outgoingMessagePump = SubscribeViaSocket(() => handler(controller, args, getIncoming), socket);
                    var incomingMessagePump = SubscribeManyToSocket(socket, incoming);

                    // Close the socket when either pump finishes.
                    await Task.WhenAny(outgoingMessagePump, incomingMessagePump);
                }
            };
        }

        /// <summary>
        /// Pumps incoming messages from <paramref name="socket"/> into their corresponding observables.
        /// </summary>
        /// <param name="socket">
        /// The socket.
        /// </param>
        /// <param name="incomingObservables">
        /// The incoming observables.
        /// </param>
        /// <returns>
        /// A <see cref="Task"/> which completes when an error occurs, the socket closes, or .
        /// </returns>
        private static async Task SubscribeManyToSocket(WebSocket socket, Dictionary<string, Subject<string>> incomingObservables)
        {
            while (!socket.IsClosed)
            {
                try
                {
                    var message = await socket.ReceiveString();
                    if (string.IsNullOrWhiteSpace(message))
                    {
                        continue;
                    }

                    // Get the name of the incoming observable.
                    var nameIndex = message.IndexOf('.');
                    if (nameIndex == 0)
                    {
                        // Invalid message.
                        continue;
                    }

                    var name = message.Substring(1, nameIndex - 1).ToLowerInvariant();

                    // Retrieve the named observable.
                    Subject<string> observable;
                    if (!incomingObservables.TryGetValue(name, out observable))
                    {
                        // Observable not found, meaning that the controller method does not care about this observable.
                        continue;
                    }

                    // Route the message to the correct method.
                    switch (message[0])
                    {
                        case 'n':
                        {
                            // OnNext
                            var payload = message.Substring(nameIndex + 1);
                            observable.OnNext(payload);
                            break;
                        }

                        case 'e':
                        {
                            // OnError
                            var payload = message.Substring(nameIndex + 1);
                            observable.OnError(new Exception(payload));

                            // Remove the observable, it will never be called again.
                            incomingObservables.Remove(name);
                            break;
                        }

                        case 'c':
                        {
                            // OnCompleted
                            observable.OnCompleted();

                            // Remove the observable, it will never be called again.
                            incomingObservables.Remove(name);
                            break;
                        }
                    }
                }
                catch (Exception)
                {
                    // An error occurred, exit.
                    break;
                }

                if (!incomingObservables.Any())
                {
                    // No more observables, exit.
                    break;
                }
            }

            foreach (var observable in incomingObservables.Values)
            {
                observable.OnCompleted();
            }
        }

        /// <summary>
        /// Subscribes to the provided <paramref name="handlerFunc"/>, sending all events to the provided <paramref name="socket"/>.
        /// </summary>
        /// <param name="handlerFunc">
        /// The observable.
        /// </param>
        /// <param name="socket">
        /// The socket.
        /// </param>
        /// <returns>
        /// The <see cref="Task"/> which will complete when either the observable completes (or errors) or the socket is closed (or errors).
        /// </returns>
        private static async Task SubscribeViaSocket(Func<IObservable<string>> handlerFunc, WebSocket socket)
        {
            var completion = new TaskCompletionSource<int>();
            var subscription = new IDisposable[] { null };
            
            // Capture any exceptionss from the handler so that they can be propagated.
            IObservable<string> outgoing;
            try
            {
                outgoing = handlerFunc();
            }
            catch (Exception exception)
            {
                outgoing = Observable.Throw<string>(exception);
            }

            Action complete = () =>
            {
                if (subscription[0] != null)
                {
                    subscription[0].Dispose();
                }

                completion.TrySetResult(0);
            };

            // Until the socket is closed, pipe messages to it, then complete.
            subscription[0] = outgoing.TakeWhile(_ => !socket.IsClosed).SelectMany(
                next => socket.Send('n' + next).ToObservable(),
                error => socket.Send('e' + JsonConvert.SerializeObject(error.Message)).ToObservable(),
                () =>
                {
                    if (!socket.IsClosed)
                    {
                        // Send and close the socket.
                        var send = socket.Send("c").ToObservable();
                        return send.SelectMany(_ => socket.Close((int)WebSocketCloseStatus.NormalClosure, "onCompleted").ToObservable());
                    }

                    return Observable.Empty<Unit>();
                }).Subscribe(_ => { }, _ => complete(), complete);
            await completion.Task;
        }
    }
}