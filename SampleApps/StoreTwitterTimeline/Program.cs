﻿using BigQuery.Linq;
using CoreTweet;
using CoreTweet.Streaming;
using CoreTweet.Streaming.Reactive;
using Google.Apis.Bigquery.v2.Data;
using Google.Apis.Util;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Reflection;

namespace StoreTwitterTimeline
{
    class Program
    {
        static TableFieldSchema[] CreateTwitterStatusSchemas()
        {
            var schemas = DataTypeUtility.ToTableFieldSchema(typeof(Status), (pi) =>
            {
                // avoid circular reference
                if (pi == typeof(Place).GetProperty("ContainedWithin"))
                {
                    return new TableFieldSchema
                    {
                        Name = "contained_within_id",
                        Type = DataType.String.ToIdentifier(),
                        Mode = "REPEATED"
                    };
                }
                if (pi == typeof(Status).GetProperty("RetweetedStatus"))
                {
                    return new TableFieldSchema
                    {
                        Name = "retweeted_status_id",
                        Type = DataType.Integer.ToIdentifier(),
                    };
                }
                if (pi == typeof(User).GetProperty("Status"))
                {
                    return new TableFieldSchema
                    {
                        Name = "status_id",
                        Type = DataType.Integer.ToIdentifier()
                    };
                }

                // avoid complex type

                if (pi.PropertyType == typeof(double[][][])) // Place/BoundingBox/Coordinates
                {
                    return new TableFieldSchema
                    {
                        Name = pi.GetCustomAttribute<JsonPropertyAttribute>().PropertyName,
                        Type = DataType.String.ToIdentifier()
                    };
                }

                // Type mapping
                if (pi.PropertyType == typeof(Uri))
                {
                    return new TableFieldSchema
                    {
                        Name = pi.GetCustomAttribute<JsonPropertyAttribute>().PropertyName,
                        Type = DataType.String.ToIdentifier()
                    };
                }
                if (pi.PropertyType == typeof(Dictionary<string, object>))
                {
                    return new TableFieldSchema
                    {
                        Name = pi.GetCustomAttribute<JsonPropertyAttribute>().PropertyName,
                        Type = DataType.String.ToIdentifier()
                    };
                }

                return null;
            });

            return schemas;
        }


        class StatusResolver : Newtonsoft.Json.Serialization.DefaultContractResolver
        {
            protected override IList<Newtonsoft.Json.Serialization.JsonProperty> CreateProperties(Type type, MemberSerialization memberSerialization)
            {
                var result = base.CreateProperties(type, memberSerialization);

                if (type == typeof(Place))
                {
                    var target = result.First(x => x.PropertyName == "contained_within");
                    target.PropertyName = "contained_within_id";
                    target.PropertyType = typeof(string[]);
                    target.ValueProvider = new GenericValueProvider<Place>(x => (x.ContainedWithin != null) ? x.ContainedWithin.Select(y => y.Id).ToArray() : new string[0]);
                }
                if (type == typeof(Status))
                {
                    var target = result.First(x => x.PropertyName == "retweeted_status");
                    target.PropertyName = "retweeted_status_id";
                    target.PropertyType = typeof(long?);
                    target.ValueProvider = new GenericValueProvider<Status>(x => (x.RetweetedStatus != null) ? (long?)x.RetweetedStatus.Id : null);
                }
                if (type == typeof(User))
                {
                    var target = result.First(x => x.PropertyName == "status");
                    target.PropertyName = "status_id";
                    target.PropertyType = typeof(long?);
                    target.ValueProvider = new GenericValueProvider<User>(x => (x.Status != null) ? (long?)x.Status.Id : null);
                }
                if (type == typeof(BoundingBox))
                {
                    var target = result.First(x => x.PropertyName == "coordinates");
                    target.PropertyType = typeof(string);
                    target.ValueProvider = new GenericValueProvider<BoundingBox>(x => JsonConvert.SerializeObject(x.Coordinates));
                }
                if (type == typeof(Coordinates))
                {
                    var target = result.First(x => x.PropertyName == "coordinates");
                    target.Ignored = true; // ignore double private field
                }

                return result;
            }
        }

        class GenericValueProvider<T> : IValueProvider
        {
            readonly Func<T, object> getValue;

            public GenericValueProvider(Func<T, object> getValue)
            {
                this.getValue = getValue;
            }

            public object GetValue(object target)
            {
                return getValue((T)target);
            }

            public void SetValue(object target, object value)
            {

            }
        }

        class ErrorTable
        {
            public DateTimeOffset Timestamp { get; set; }
            public string Type { get; set; }
            public string StackTrace { get; set; }
            public string Message { get; set; }
            public string Source { get; set; }
        }

        static void CreateTable()
        {
            var context = Account.GetContext();
            var service = context.BigQueryService;
            var schema = CreateTwitterStatusSchemas();

            new MetaTable(context.ProjectId, "twitter", "sample")
                .CreateTable(service, schema, "Twitter Streaming Timeline:Sample");

            new MetaTable(context.ProjectId, "twitter", "user")
                .CreateTable(service, schema, "Twitter Streaming Timeline:User");

            new MetaTable(context.ProjectId, "twitter", "error")
                .CreateTable(service, DataTypeUtility.ToTableFieldSchema<ErrorTable>());
        }

        static IObservable<long> InsertStatus(IObservable<StreamingMessage> stream, BigQueryContext context, MetaTable insertTable)
        {
            var resolverSettings = new JsonSerializerSettings { ContractResolver = new StatusResolver() };
            var errorTable = new MetaTable(context.ProjectId, "twitter", "error");

            var count = 0L;
            return stream
                .OfType<StatusMessage>()
                .Select(x => x.Status)
                .Buffer(TimeSpan.FromSeconds(10), 100)
                .SelectMany(tweets =>
                {
                    count += tweets.Count;
                    return insertTable.InsertAllAsync(
                            context.BigQueryService,
                            tweets,
                            new ExponentialBackOff(TimeSpan.FromMilliseconds(250), 3),
                            insertIdSelector: x => x.Id.ToString(),
                            serializerSettings: resolverSettings)
                        .ToObservable();
                })
                .Do(_ => { }, ex =>
                {
                    string message;
                    if (ex is InsertAllFailedException)
                    {
                        var failed = (ex as InsertAllFailedException);
                        if (failed.InnerException != null)
                        {
                            ex = failed.InnerException;
                            message = ex.Message;
                        }
                        else
                        {
                            message = string.Join("\r\n\r\n", failed.InternalErrorInfos.Where(x => x.Reason != "stopped").Select(x => x.ToString()));
                        }
                    }
                    else
                    {
                        message = ex.Message;
                    }

                    Console.WriteLine(message);
                    try
                    {
                        errorTable.InsertAllAsync(context.BigQueryService, new[] { new ErrorTable { Timestamp = DateTimeOffset.UtcNow, Type = ex.GetType().Name, StackTrace = ex.StackTrace, Message = message, Source = ex.Source } }).Wait();
                    }
                    catch (Exception ex2)
                    {
                        ex = ex2;
                        Console.WriteLine(ex.ToString());
                        errorTable.InsertAllAsync(context.BigQueryService, new[] { new ErrorTable { Timestamp = DateTimeOffset.UtcNow, Type = ex.GetType().Name, StackTrace = ex.StackTrace, Message = ex.Message, Source = ex.Source } }).Wait();
                    }
                })
                .Select(_ => count)
                .Retry();
        }

        static void Main(string[] args)
        {
            // If not created table yet, commentout and create table!
            // CreateTable();
            // return;

            // Insert Twitter Streaming Data to BigQuery

            var context = Account.GetContext();
            var token = Account.GetTokens();

            var sampleInsert = InsertStatus(
                token.Streaming.StartObservableStream(StreamingType.Sample),
                context,
                new MetaTable(context.ProjectId, "twitter", "sample"));

            var userInsert = InsertStatus(
                token.Streaming.StartObservableStream(StreamingType.User),
                context,
                new MetaTable(context.ProjectId, "twitter", "user"));

            // start insert and synchronous wait
            sampleInsert.CombineLatest(userInsert, (sampleCount, userCount) => new { sampleCount, userCount })
                .Sample(TimeSpan.FromSeconds(10))
                .ForEachAsync(x =>
                {
                    Console.WriteLine(x.ToString());
                })
                .Wait();
        }
    }
}