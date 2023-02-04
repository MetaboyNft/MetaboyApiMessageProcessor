using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using LoopDropSharp;
using MetaboyApiMessageProcessor.Models;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Nethereum.Signer;
using Nethereum.Signer.EIP712;
using Nethereum.Util;
using PoseidonSharp;
using Type = LoopDropSharp.Type;
using System.Numerics;
using Dapper;
using System.Data;
using System.Globalization;
using System.Data.SqlClient;
using System.Diagnostics;
using Microsoft.Azure.Amqp.Framing;

public class Program
{
    //connection string
    static string AzureServiceBusConnectionString = "";

    // name of your Service Bus queue
    static string queueName = "main";

    // the client that owns the connection and can be used to create senders and receivers
    static ServiceBusClient client;

    // the processor that reads and processes messages from the queue
    static ServiceBusProcessor processor;

    static ILoopringService loopringService;

    static Settings settings;

    static string AzureSqlConnectionString = "";

    static async Task Main(string[] args)
    {
        // load services
        loopringService = new LoopringService();

        //Settings loaded from the appsettings.json fileq
        IConfiguration config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json")
            .AddEnvironmentVariables()
            .Build();
        settings = config.GetRequiredSection("Settings").Get<Settings>();

        AzureServiceBusConnectionString = settings.AzureServiceBusConnectionString;
        AzureSqlConnectionString = settings.AzureSqlConnectionString;

        var clientOptions = new ServiceBusClientOptions() { TransportType = ServiceBusTransportType.AmqpWebSockets };
        client = new ServiceBusClient(AzureServiceBusConnectionString, clientOptions);

        // create a processor that we can use to process the messages
        processor = client.CreateProcessor(queueName, new ServiceBusProcessorOptions() { MaxConcurrentCalls = 1, PrefetchCount = 1, AutoCompleteMessages = false });

        try
        {
            // add handler to process messages
            processor.ProcessMessageAsync += MessageHandler;

            // add handler to process any errors
            processor.ProcessErrorAsync += ErrorHandler;

            // start processing 
            await processor.StartProcessingAsync();
            Console.WriteLine("[INFO]: Waiting for messages...");
            while (true)
            {

            }
        }
        finally
        {
            // Calling DisposeAsync on client types is required to ensure that network
            // resources and other unmanaged objects are properly cleaned up.
            await processor.DisposeAsync();
            await client.DisposeAsync();
        }
    }

    //  Receives a single nftReceiverV3 (Address, NftData, Amount) from MetaBoyAPI that has already been validated
    static async Task MessageHandler(ProcessMessageEventArgs args)
    {
        string body = args.Message.Body.ToString();
        Console.WriteLine($"[INFO] Received: {body}");
        NftReciever nftReciever = JsonConvert.DeserializeObject<NftReciever>(body);
        int? validStatus = null;
        string nftAmount = "";
        AvailableClaim availableClaim = new();
        try
        {   
            //  AvailableClaims is queried to validate and obtain requested amount to send in transaction
            //  If transfer is successful, insert new CompletedClaim record and delete AvailableClaim record
            using (SqlConnection db = new System.Data.SqlClient.SqlConnection(AzureSqlConnectionString))
            {
                Console.WriteLine($"[INFO] Processing request...");
                // 0 = Claim is not valid - Abort!
                // 1 = Claim is valid, CompletedClaims record not found, new record Insert will be required, AllowableClaims record will be deleted.
                // 2 = Multiple Claim Records found! Please validate data!

                
                // Validate record with Address, NftData, and Amount
                await db.OpenAsync();
                var availableClaimParameters = new { NftData = nftReciever.NftData, Address = nftReciever.Address };
                var availableClaimSql = "SELECT * FROM AvailableClaims WHERE nftdata = @NftData AND Address = @Address";
                var availableClaimResult = await db.QueryAsync<AvailableClaim>(availableClaimSql, availableClaimParameters);
                
                // We should only have 1 NftData entry in Claimable Table if Nft is claimable, with a valid Amount greater than 0
                // Set it as a new instance of AvailableClaim with Address, NftData, & Amount
                if (availableClaimResult.Count() == 1 && int.Parse(availableClaimResult.First().Amount) > 0)
                {
                    availableClaim = availableClaimResult.First();
                    Console.WriteLine($"[INFO]  Found Available Claim: Address {availableClaim.Address} NftData: {availableClaim.NftData} For: {availableClaim.Amount} Nfts.");
                    validStatus = 1;
                }

                // More than 1 record retrieved - Data validation required!
                else if (availableClaimResult.Count() > 1)
                {
                    foreach (AvailableClaim claimRecord in availableClaimResult)
                    {
                        Console.WriteLine($"[ERROR]  Multiple Claim Records found in AvailableClaims For Address: {claimRecord.Address} NftData: {claimRecord.NftData} For {claimRecord.Amount} Nfts!)");
                        validStatus = 2;
                    }
                }

                // Unexpected outcome - Abort!
                else
                {
                    Console.WriteLine($"[ERROR]  Unexpected outcome detected! Please check Address: {nftReciever.Address} NftData: {nftReciever.NftData}");
                    validStatus = 0;
                }

                await db.CloseAsync();
            }
        }

        catch (Exception ex)
        {
            validStatus = 0;
            Console.WriteLine(ex.Message);
        }

        if (validStatus == 1)
        {
            string loopringApiKey = settings.LoopringApiKey;             // Loopring api key KEEP PRIVATE
            string loopringPrivateKey = settings.LoopringPrivateKey;     // Loopring private key KEEP PRIVATE
            var MMorGMEPrivateKey = settings.MMorGMEPrivateKey;          // Metamask or gamestop private key KEEP PRIVATE
            var fromAddress = settings.LoopringAddress;                  // Your loopring address
            var fromAccountId = settings.LoopringAccountId;              // Your loopring account id
            var validUntil = settings.ValidUntil;                        // Loopring examples use this number
            var maxFeeTokenId = settings.MaxFeeTokenId;                  // 0 should be for ETH, 1 is for LRC
            var exchange = settings.Exchange;                            //loopring exchange address, shouldn't need to change this,
            int toAccountId = 0;                                         //leave this as 0 DO NOT CHANGE
            int nftTokenId;
            NftBalance userNftToken = new NftBalance();
            string nftData = nftReciever.NftData;
            string transferMemo = settings.TransferMemo;
            try
            {
                userNftToken = await loopringService.GetTokenIdWithCheck(settings.LoopringApiKey, settings.LoopringAccountId, nftData);
                nftTokenId = userNftToken.data[0].tokenId;
                var toAddress = nftReciever.Address;

                //Storage id
                var storageId = await loopringService.GetNextStorageId(loopringApiKey, fromAccountId, nftTokenId);

                //Getting the offchain fee
                var offChainFee = await loopringService.GetOffChainFee(loopringApiKey, fromAccountId, 11, "0");

                //Calculate eddsa signautre
                BigInteger[] poseidonInputs =
                    {
                                    Utils.ParseHexUnsigned(exchange),
                                    (BigInteger) fromAccountId,
                                    (BigInteger) toAccountId,
                                    (BigInteger) nftTokenId,
                                    BigInteger.Parse(nftAmount),
                                    (BigInteger) maxFeeTokenId,
                                    BigInteger.Parse(offChainFee.fees[maxFeeTokenId].fee),
                                    Utils.ParseHexUnsigned(toAddress),
                                    (BigInteger) 0,
                                    (BigInteger) 0,
                                    (BigInteger) validUntil,
                                    (BigInteger) storageId.offchainId
                    };
                Poseidon poseidon = new Poseidon(13, 6, 53, "poseidon", 5, _securityTarget: 128);
                BigInteger poseidonHash = poseidon.CalculatePoseidonHash(poseidonInputs);
                Eddsa eddsa = new Eddsa(poseidonHash, loopringPrivateKey);
                string eddsaSignature = eddsa.Sign();

                //Calculate ecdsa
                string primaryTypeName = "Transfer";
                TypedData eip712TypedData = new TypedData();
                eip712TypedData.Domain = new Domain()
                {
                    Name = "Loopring Protocol",
                    Version = "3.6.0",
                    ChainId = 1,
                    VerifyingContract = "0x0BABA1Ad5bE3a5C0a66E7ac838a129Bf948f1eA4",
                };
                eip712TypedData.PrimaryType = primaryTypeName;
                eip712TypedData.Types = new Dictionary<string, MemberDescription[]>()
                {
                    ["EIP712Domain"] = new[]
                        {
                                            new MemberDescription {Name = "name", Type = "string"},
                                            new MemberDescription {Name = "version", Type = "string"},
                                            new MemberDescription {Name = "chainId", Type = "uint256"},
                                            new MemberDescription {Name = "verifyingContract", Type = "address"},
                                        },
                    [primaryTypeName] = new[]
                        {
                                            new MemberDescription {Name = "from", Type = "address"},            // payerAddr
                                            new MemberDescription {Name = "to", Type = "address"},              // toAddr
                                            new MemberDescription {Name = "tokenID", Type = "uint16"},          // token.tokenId 
                                            new MemberDescription {Name = "amount", Type = "uint96"},           // token.volume 
                                            new MemberDescription {Name = "feeTokenID", Type = "uint16"},       // maxFee.tokenId
                                            new MemberDescription {Name = "maxFee", Type = "uint96"},           // maxFee.volume
                                            new MemberDescription {Name = "validUntil", Type = "uint32"},       // validUntill
                                            new MemberDescription {Name = "storageID", Type = "uint32"}         // storageId
                                        },

                };
                eip712TypedData.Message = new[]
                {
                                    new MemberValue {TypeName = "address", Value = fromAddress},
                                    new MemberValue {TypeName = "address", Value = toAddress},
                                    new MemberValue {TypeName = "uint16", Value = nftTokenId},
                                    new MemberValue {TypeName = "uint96", Value = BigInteger.Parse(nftAmount)},
                                    new MemberValue {TypeName = "uint16", Value = maxFeeTokenId},
                                    new MemberValue {TypeName = "uint96", Value = BigInteger.Parse(offChainFee.fees[maxFeeTokenId].fee)},
                                    new MemberValue {TypeName = "uint32", Value = validUntil},
                                    new MemberValue {TypeName = "uint32", Value = storageId.offchainId},
                                };

                TransferTypedData typedData = new TransferTypedData()
                {
                    domain = new TransferTypedData.Domain()
                    {
                        name = "Loopring Protocol",
                        version = "3.6.0",
                        chainId = 1,
                        verifyingContract = "0x0BABA1Ad5bE3a5C0a66E7ac838a129Bf948f1eA4",
                    },
                    message = new TransferTypedData.Message()
                    {
                        from = fromAddress,
                        to = toAddress,
                        tokenID = nftTokenId,
                        amount = nftAmount,
                        feeTokenID = maxFeeTokenId,
                        maxFee = offChainFee.fees[maxFeeTokenId].fee,
                        validUntil = (int)validUntil,
                        storageID = storageId.offchainId
                    },
                    primaryType = primaryTypeName,
                    types = new TransferTypedData.Types()
                    {
                        EIP712Domain = new List<Type>()
                                        {
                                            new Type(){ name = "name", type = "string"},
                                            new Type(){ name="version", type = "string"},
                                            new Type(){ name="chainId", type = "uint256"},
                                            new Type(){ name="verifyingContract", type = "address"},
                                        },
                        Transfer = new List<Type>()
                                        {
                                            new Type(){ name = "from", type = "address"},
                                            new Type(){ name = "to", type = "address"},
                                            new Type(){ name = "tokenID", type = "uint16"},
                                            new Type(){ name = "amount", type = "uint96"},
                                            new Type(){ name = "feeTokenID", type = "uint16"},
                                            new Type(){ name = "maxFee", type = "uint96"},
                                            new Type(){ name = "validUntil", type = "uint32"},
                                            new Type(){ name = "storageID", type = "uint32"},
                                        }
                    }
                };

                Eip712TypedDataSigner signer = new Eip712TypedDataSigner();
                var ethECKey = new Nethereum.Signer.EthECKey(MMorGMEPrivateKey.Replace("0x", ""));
                var encodedTypedData = signer.EncodeTypedData(eip712TypedData);
                var ECDRSASignature = ethECKey.SignAndCalculateV(Sha3Keccack.Current.CalculateHash(encodedTypedData));
                var serializedECDRSASignature = EthECDSASignature.CreateStringSignature(ECDRSASignature);
                var ecdsaSignature = serializedECDRSASignature + "0" + (int)2;

                // DEBUG //
                Console.WriteLine($"[DEBUG]  SKIPPING TRANSFER FOR TESTING PURPOSES!!!");
                /*
                //Submit nft transfer
                var nftTransferResponse = await loopringService.SubmitNftTransfer(
                    apiKey: loopringApiKey,
                    exchange: exchange,
                    fromAccountId: fromAccountId,
                    fromAddress: fromAddress,
                    toAccountId: toAccountId,
                    toAddress: toAddress,
                    nftTokenId: nftTokenId,
                    nftAmount: nftAmount,
                    maxFeeTokenId: maxFeeTokenId,
                    maxFeeAmount: offChainFee.fees[maxFeeTokenId].fee,
                    storageId.offchainId,
                    validUntil: validUntil,
                    eddsaSignature: eddsaSignature,
                    ecdsaSignature: ecdsaSignature,
                    nftData: nftData,
                    transferMemo: transferMemo
                    );
                Console.WriteLine(nftTransferResponse);
                

                if (nftTransferResponse.Contains("process") || nftTransferResponse.Contains("received"))
                //*/
                
                if (1 == 1) //</-- Debug -->
                {
                    try
                    {
                        // If Claimed record not found, go ahead and insert record into Claimed
                        using (SqlConnection db = new System.Data.SqlClient.SqlConnection(AzureSqlConnectionString))
                        {
                            await db.OpenAsync();
                            var insertParameters = new
                            {
                                Address = availableClaim.Address,
                                NftData = availableClaim.NftData,
                                ClaimedDate = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fffffff",
                                       CultureInfo.InvariantCulture),
                                Amount = availableClaim.Amount
                            };
                            // Create an entry in CompletedClaims Table
                            var recordedClaim = await db.ExecuteAsync("INSERT INTO CompletedClaims (Address, NftData, ClaimedDate, Amount) VALUES (@Address, @NftData, @ClaimedDate, @Amount");

                            // Record successfully added to CompletedClaims
                            if (recordedClaim == 0)
                            {
                                var removeFromAvailableClaimsParameters = new
                                {
                                    removeAddress = availableClaim.Address,
                                    removeNftData = availableClaim.NftData
                                };
                                var deleteAvailableClaim = await db.ExecuteAsync("DELETE From allowlist where address = @Address and nftdata = @NftData", removeFromAvailableClaimsParameters);
                                Console.WriteLine($"[INFO]  Claim completed, record removed from AvailableClaims Table. Record has been added to CompletedClaims For Address: {nftReciever.Address} NftData: {nftReciever.NftData}");
                            }
                            else
                            {
                                Console.WriteLine($"[ERROR]  Unable to insert record into CompletedClaims Table! Record has not beed deleted from AvailableClaims! Please check Address: {nftReciever.Address} NftData: {nftReciever.NftData}");
                                validStatus = 0;
                            }

                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ERROR] An error has occured processing database record update! BONK!");
                        Console.WriteLine(ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
            finally
            {
                try
                {
                    await args.CompleteMessageAsync(args.Message);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message);
                }

            }
        }
        else if (validStatus == 0)
        {
            try
            {
                await args.CompleteMessageAsync(args.Message);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }
        else if (validStatus == 2)
        {
            try
            {
                await args.CompleteMessageAsync(args.Message);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }
        else
        {
            try
            {
                Console.WriteLine($"[ERROR] Something went wrong with address: {nftReciever.Address}, and nft: {nftReciever.NftData}");
                await args.CompleteMessageAsync(args.Message);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }
    }

    // handle any errors when receiving messages
    static Task ErrorHandler(ProcessErrorEventArgs args)
    {
        Console.WriteLine(args.Exception.ToString());
        return Task.CompletedTask;
    }

}