using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Grpc.Core;
using Lnrpc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using ServiceStack.Text; 


namespace LNDStreamingBackup
{
    class Program
    {
        async static Task Main(string[] args)
        {
        
            

            if (args.Length < 4)
            {
                Console.WriteLine("Valid usage: LNDStreamingBackup <path to tls.cert> <path to macaroon> <Azure storage container connection string> <blob container to save to>");
                return;
            }
            // Create a BlobServiceClient object which will be used to create a container client
            BlobServiceClient blobServiceClient = new BlobServiceClient(args[2]);
            BlobContainerClient containerClient = blobServiceClient.GetBlobContainerClient(args[3]);
            // Due to updated ECDSA generated tls.cert we need to let gprc know that
            // we need to use that cipher suite otherwise there will be a handshake
            // error when we communicate with the lnd rpc server.
            System.Environment.SetEnvironmentVariable("GRPC_SSL_CIPHER_SUITES", "HIGH+ECDSA");
            string connectionString = Environment.GetEnvironmentVariable("AZURE_STORAGE_CONNECTION_STRING");

            var cert = File.ReadAllText(args[0]);
            var sslCreds = new SslCredentials(cert);

            byte[] macaroonBytes = File.ReadAllBytes(args[1]);
            var macaroon = BitConverter.ToString(macaroonBytes).Replace("-", ""); // hex format stripped of "-" chars


            // combine the cert credentials and the macaroon auth credentials using interceptors
            // so every call is properly encrypted and authenticated
            Task AddMacaroon(AuthInterceptorContext context, Metadata metadata)
            {
                metadata.Add(new Metadata.Entry("macaroon", macaroon));
                return Task.CompletedTask;
            }
            var macaroonInterceptor = new AsyncAuthInterceptor(AddMacaroon);
            var combinedCreds = ChannelCredentials.Create(sslCreds, CallCredentials.FromInterceptor(macaroonInterceptor));

            // finally pass in the combined credentials when creating a channel
            var channel = new Grpc.Core.Channel("localhost:10009", combinedCreds);
            var client = new Lnrpc.Lightning.LightningClient(channel);
            var info = await client.GetInfoAsync(new GetInfoRequest());  //Get node info

            //make sure we can save stuff before we go and try to run gRPC loop
            try
            {
                var backup = await client.ExportAllChannelBackupsAsync(new ChanBackupExportRequest());
                BlobClient blobClient = containerClient.GetBlobClient($"{info.IdentityPubkey}.SingleChanBackups.bak");
                BlobClient blobClient2 = containerClient.GetBlobClient($"{info.IdentityPubkey}.MultiChanBackup.bak");
                await blobClient.UploadAsync(new MemoryStream(Encoding.UTF8.GetBytes(backup.SingleChanBackups.ToString())),true);
                await blobClient2.UploadAsync(new MemoryStream(Encoding.UTF8.GetBytes(backup.MultiChanBackup.ToString())), true);
            }
            catch(Exception e)
            {
                Console.Write($"Something went wrong with saving to Azure Blob Storage, check your settings: {e}");
            }
   

            using (var call = client.SubscribeChannelBackups(new ChannelBackupSubscription()))
            {
                while (await call.ResponseStream.MoveNext())
                {
                    var backup = call.ResponseStream.Current;
                    BlobClient blobClient = containerClient.GetBlobClient($"{info.IdentityPubkey}.SingleChanBackups.bak");
                    BlobClient blobClient2 = containerClient.GetBlobClient($"{info.IdentityPubkey}.MultiChanBackup.bak");
                    await blobClient.UploadAsync(new MemoryStream(Encoding.UTF8.GetBytes(backup.SingleChanBackups.ToString())), true);
                    await blobClient2.UploadAsync(new MemoryStream(Encoding.UTF8.GetBytes(backup.MultiChanBackup.ToString())), true);
                }
            }

            await channel.ShutdownAsync();
        }
    }

}
