using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ServerlessMapReduce
{
    public static class Sample
    {
        private static readonly HttpClient _httpClient = HttpClientFactory.Create();

        [FunctionName(nameof(StartAsync))]
        public static async Task<HttpResponseMessage> StartAsync([HttpTrigger(AuthorizationLevel.Function, "post")]HttpRequestMessage req,
            [DurableClient] IDurableClient starter,
            ILogger log)
        {
            // retrieve storage blobs URI of the taxi dataset
            var pathString = req.RequestUri.ParseQueryString()[@"path"] ?? throw new ArgumentNullException(@"required query string parameter 'path' not found");
            var path = new Uri(pathString);

            var containerUrl = path.GetLeftPart(UriPartial.Authority) + "/" + path.Segments[1];
            var prefix = string.Join(string.Empty, path.Segments.Skip(2));

            log.LogInformation(@"Starting orchestrator...");
            var newInstanceId = await starter.StartNewAsync(nameof(BeginMapReduce), new { containerUrl, prefix });
            log.LogInformation($@"- Instance id: {newInstanceId}");

            return starter.CreateCheckStatusResponse(req, newInstanceId);
        }

        [FunctionName(nameof(BeginMapReduce))]
        public static async Task<string> BeginMapReduce([OrchestrationTrigger]IDurableOrchestrationContext context, ILogger log)
        {
            var input = context.GetInput<JObject>();

            //container URL of Azure storage blob
            var containerUrl = input["containerUrl"].ToString();

            //prefix string of Azure storage blob
            var prefix = input["prefix"].ToString();

            if (!context.IsReplaying)
            {
                log.LogInformation($@"Beginning MapReduce. Container: {containerUrl} | Prefix: {prefix}");
                context.SetCustomStatus(new { status = @"Getting files to reduce", containerUrl, prefix });
            }
            //retrieve storage Uri of each file via an Activity function
            var files = await context.CallActivityAsync<string[]>(
                nameof(GetFileListAsync),
                new string[] { containerUrl, prefix });

            if (!context.IsReplaying)
            {
                log.LogInformation($@"{files.Length} file(s) found: {JsonConvert.SerializeObject(files)}");
                log.LogInformation(@"Creating mappers...");
                context.SetCustomStatus(new { status = @"Creating mappers", files });
            }
            //create mapper tasks which download and calculate avg speed from each csv file
            var tasks = new Task<double[]>[files.Length];
            for (var i = 0; i < files.Length; i++)
            {
                tasks[i] = context.CallActivityAsync<double[]>(
                    nameof(MapperAsync),
                    files[i]);
            }

            if (!context.IsReplaying)
            {
                log.LogInformation($@"Waiting for all {files.Length} mappers to complete...");
                context.SetCustomStatus(@"Waiting for mappers");
            }
            //wait all tasks done
            await Task.WhenAll(tasks);

            if (!context.IsReplaying)
            {
                log.LogInformation(@"Executing reducer...");
                context.SetCustomStatus(@"Executing reducer");
            }
            //create reducer activity function for aggregating result 
            var result = await context.CallActivityAsync<string>(
                nameof(Reducer),
                tasks.Select(task => task.Result).ToArray());

            log.LogInformation($@"**FINISHED** Result: {result}");
            context.SetCustomStatus(null);
            //log.LogInformationNoReplay(context,@"Writing result to blob...");
            //await context.CallActivityAsync(
            //    nameof(WriteToBlob),
            //    result);

            //output result
            return result;
        }

        /// <summary>
        /// GetFileList Activity function for retrieving all storage blob files Uri
        /// </summary>
        [FunctionName(nameof(GetFileListAsync))]
        public static async Task<string[]> GetFileListAsync(
            [ActivityTrigger] string[] paras)
        {
            var cloudBlobContainer = new CloudBlobContainer(new Uri(paras[0]));

            var blobs = Enumerable.Empty<IListBlobItem>();
            var continuationToken = default(BlobContinuationToken);
            do
            {
                var segmentBlobs = await cloudBlobContainer.ListBlobsSegmentedAsync(paras[1], continuationToken);
                blobs = blobs.Concat(segmentBlobs.Results);

                continuationToken = segmentBlobs.ContinuationToken;
            } while (continuationToken != null);

            return blobs.Select(i => i.Uri.ToString()).ToArray();
        }

        /// <summary>
        /// Mapper Activity function to download and parse a CSV file
        /// </summary>
        [FunctionName(nameof(MapperAsync))]
        public static async Task<IEnumerable<double>> MapperAsync(
            [ActivityTrigger] string fileUri,
            ILogger log)
        {
            log.LogInformation($@"Executing mapper for {fileUri}...");
            var speedsByDayOfWeek = new double[7];
            var numberOfLogsPerDayOfWeek = new int[7];

            // download blob file
#pragma warning disable IDE0067 // Dispose objects before losing scope
            // Don't wrap in a Using because this was causing occasional ObjectDisposedException errors in v2 executions
            var reader = new StreamReader(await _httpClient.GetStreamAsync(fileUri));
#pragma warning restore IDE0067 // Dispose objects before losing scope

            var lineText = string.Empty;
            // read a line from NetworkStream
            while (!reader.EndOfStream && (lineText = await reader.ReadLineAsync()) != null)
            {
                // parse CSV format line
                var segdata = lineText.Split(',');

                // If it is header line or blank line, then continue
                if (segdata.Length != 17 || !int.TryParse(segdata[0], out var n))
                {
                    continue;
                }

                // retrieve the value of pickup_datetime column
                var pickup_datetime = DateTime.Parse(segdata[1]);

                // retrieve the value of dropoff_datetime column
                var dropoff_datetime = DateTime.Parse(segdata[2]);

                // retrieve the value of trip_distance column
                var trip_distance = Convert.ToDouble(segdata[4]);

                if (trip_distance > 0)
                {
                    double avgSpeed;
                    try
                    {
                        // calculate avg speed
                        avgSpeed = trip_distance / dropoff_datetime.Subtract(pickup_datetime).TotalHours;

                        if (double.IsInfinity(avgSpeed) || double.IsNaN(avgSpeed))
                        {
                            continue;
                        }
                    }
#pragma warning disable CA1031 // Do not catch general exception types
                    catch (DivideByZeroException)
                    {   // skip it
                        continue;
                    }
#pragma warning restore CA1031 // Do not catch general exception types

                    // sum of avg speed by each day of week
                    speedsByDayOfWeek[(int)pickup_datetime.DayOfWeek] += avgSpeed;

                    // number of trip by each day of week
                    numberOfLogsPerDayOfWeek[(int)pickup_datetime.DayOfWeek]++;
                }
            }

            var results = numberOfLogsPerDayOfWeek
                .Select((val, idx) => val != 0 ? speedsByDayOfWeek[idx] / val : 0)
                .AsParallel()
                .ToList();

            log.LogInformation($@"{fileUri} mapper complete. Returning {results.Count} result(s)");
            return results;
        }

        /// <summary>
        /// Reducer Activity function for results aggregation
        /// </summary>
        [FunctionName(nameof(Reducer))]
        public static string Reducer(
            [ActivityTrigger] double[][] mapresults,
            ILogger log)
        {
            log.LogInformation(@"Reducing results...");

            var reduceResult = new double[7];
            // aggregate the result by each result produced via Mapper activity function.
            var mapResultsLength = mapresults.Length;
            Parallel.For(0, mapResultsLength, i =>
            {
                for (var j = 0; j < 7; j++)
                {
                    reduceResult[j] += mapresults[i][j];
                }
            });

            var retVal = string.Format("Sun: {0}, Mon: {1}, Tue : {2}, wed: {3}, Thu: {4}, Fri: {5}, Sat: {6}",
                reduceResult[0] / mapResultsLength,
                reduceResult[1] / mapResultsLength,
                reduceResult[2] / mapResultsLength,
                reduceResult[3] / mapResultsLength,
                reduceResult[4] / mapResultsLength,
                reduceResult[5] / mapResultsLength,
                reduceResult[6] / mapResultsLength
            );

            log.LogInformation(retVal);

            // return aggregation result
            return retVal;
        }

        /// <summary>
        /// WriteToBlob Activity function for storing output result to blob storage. 
        /// </summary>
        [FunctionName(nameof(WriteToBlob))]
        public static async Task WriteToBlob(
            [ActivityTrigger] string content)
        {
            var storageConnectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage");
            var storageAccount = CloudStorageAccount.Parse(storageConnectionString);

            // Create the CloudBlobClient that represents the Blob storage endpoint for the storage account.
            var cloudBlobClient = storageAccount.CreateCloudBlobClient();

            // Create a container called 'quickstartblobs' and append a GUID value to it to make the name unique. 
            var cloudBlobContainer = cloudBlobClient.GetContainerReference("result");
            await cloudBlobContainer.CreateAsync();

            var fileName = string.Format("mapreduce_sample_result_{0}.txt", DateTime.Now.ToString("yyyy_dd_M_HH_mm_ss"));
            var cloudBlockBlob = cloudBlobContainer.GetBlockBlobReference(fileName);
            await cloudBlockBlob.UploadTextAsync(content);
        }
    }
}