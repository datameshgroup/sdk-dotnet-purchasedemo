using DataMeshGroup.Fusion;
using DataMeshGroup.Fusion.Model;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace PurchaseDemo
{
    public class PurchaseDemoEvents : IDisposable
    {
        private readonly IFusionClient fusionClient;
        private TransactionIdentification saleTransactionID;

        private bool paymentResult;

        private TransactionStatusResponse transactionStatusResponse;

        private ManualResetEvent paymentResponseReceived;
        private readonly ManualResetEvent loginResponseReceived;
        private readonly ManualResetEvent transactionStatusResponseReceived;

        public PurchaseDemoEvents(string saleID, string poiID, string kek, LoginRequest loginRequest)
        {
            fusionClient = new FusionClient(true)
            {
                SaleID = saleID,
                POIID = poiID,
                KEK = kek,
                LoginRequest = loginRequest
            };
            fusionClient.OnLog += FusionClient_OnLog;
            fusionClient.OnConnect += FusionClient_OnConnect;
            fusionClient.OnConnectError += FusionClient_OnConnectError;
            fusionClient.OnDisconnect += FusionClient_OnDisconnect;
            fusionClient.OnLoginResponse += FusionClient_OnLoginResponse;
            fusionClient.OnPaymentResponse += FusionClient_OnPaymentResponse;
            fusionClient.OnTransactionStatusResponse += FusionClient_OnTransactionStatusResponse;

            paymentResponseReceived = new ManualResetEvent(false);
            loginResponseReceived = new ManualResetEvent(false);
            transactionStatusResponseReceived = new ManualResetEvent(false);
        }

        private void FusionClient_OnTransactionStatusResponse(object sender, MessagePayloadEventArgs<TransactionStatusResponse> e)
        {
            Console.WriteLine("OnTransactionStatusResponse");
            transactionStatusResponse = e.MessagePayload;
            transactionStatusResponseReceived.Set();
        }

        private void FusionClient_OnLog(object sender, LogEventArgs e)
        {
            Console.WriteLine($"{e.CreatedDateTime.ToString("HH:mm:ss.fff")}\t{Enum.GetName(e.LogLevel)}\t{e.Data}");
        }

        private void FusionClient_OnDisconnect(object sender, EventArgs e)
        {
            Console.WriteLine("OnDisconnect");
            paymentResponseReceived.Set();
            loginResponseReceived.Set();
            transactionStatusResponseReceived.Set();
        }

        private void FusionClient_OnConnectError(object sender, EventArgs e)
        {
            Console.WriteLine("OnConnectError");
            paymentResponseReceived.Set();
            loginResponseReceived.Set();
            transactionStatusResponseReceived.Set();
        }

        private void FusionClient_OnConnect(object sender, EventArgs e)
        {
            Console.WriteLine("OnConnect");
        }

        private void FusionClient_OnLoginResponse(object sender, MessagePayloadEventArgs<LoginResponse> e)
        {
            var r = e.MessagePayload;
            Console.WriteLine($"AutoLogin result = {r.Response?.Result.ToString() ?? "Unknown"}, ErrorCondition = {r.Response?.ErrorCondition}, Result = {r.Response?.AdditionalResponse}");

            loginResponseReceived.Set();
        }

        private void FusionClient_OnPaymentResponse(object sender, MessagePayloadEventArgs<PaymentResponse> e)
        {
            var r = e.MessagePayload;

            // Validate SaleTransactionID
            if (!r.SaleData.SaleTransactionID.Equals(saleTransactionID))
            {
                Console.WriteLine($"Unknown SaleId {r.SaleData.SaleTransactionID.TransactionID}");
                return;
            }

            Console.WriteLine($"Payment result = {r.Response.Result.ToString() ?? "Unknown"}, ErrorCondition = {r.Response?.ErrorCondition}, Result = {r.Response?.AdditionalResponse}");
            paymentResult = (r.Response.Result == Result.Success) || (r.Response.Result == Result.Partial);
            paymentResponseReceived.Set();
        }

        public void Dispose()
        {
            fusionClient?.Dispose();
        }

        public bool DoPayment(PaymentRequest paymentRequest)
        {
            paymentResponseReceived.Reset();
            loginResponseReceived.Reset();
            transactionStatusResponseReceived.Reset();

            SaleToPOIMessage request = null;
            saleTransactionID = paymentRequest.SaleData.SaleTransactionID;
            bool paymentResult = false;
            TimeSpan timeout = TimeSpan.FromSeconds(60);
            try
            {
                // Connect
                fusionClient.ConnectAsync().Wait(timeout);
                // Login
                fusionClient.SendAsync(fusionClient.LoginRequest).Wait(timeout);
                loginResponseReceived.WaitOne(timeout);
                // Payment
                var task = fusionClient.SendAsync(paymentRequest);
                if (task.Wait(timeout))
                {
                    request = task.Result;
                    paymentResponseReceived.WaitOne();
                }
            }
            catch (FusionException fe)
            {
                Console.WriteLine($"Exception processing payment {fe.Message} {fe.StackTrace}");
                if (fe.ErrorRecoveryRequired && request != null)
                {
                    // process error handling
                    paymentResult = HandleErrorRecovery(request);
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"Exception processing payment {e.Message} {e.StackTrace}");
            }

            return paymentResult;
        }

        private bool HandleErrorRecovery(SaleToPOIMessage request)
        {
            Console.WriteLine($"Error recovery...");

            bool result = false;

            TimeSpan timeout = TimeSpan.FromSeconds(60);
            TimeSpan requestDelay = TimeSpan.FromSeconds(10);
            Stopwatch timeoutTimer = new Stopwatch();
            timeoutTimer.Start();

            bool waitingForResponse = true;
            do
            {
                TransactionStatusRequest transactionStatusRequest = new TransactionStatusRequest()
                {
                    MessageReference = new MessageReference()
                    {
                        MessageCategory = request.MessageHeader.MessageCategory,
                        POIID = request.MessageHeader.POIID,
                        SaleID = request.MessageHeader.SaleID,
                        ServiceID = request.MessageHeader.ServiceID
                    }
                };

                try
                {
                    if (fusionClient.SendAsync(transactionStatusRequest).Wait(timeout))
                    {
                        if (transactionStatusResponseReceived.WaitOne(timeout))
                        {
                            // transactionStatusResponse set in FusionClient_OnTransactionStatusResponse

                           // If the response to our TransactionStatus request is "Success", we have a PaymentResponse to check
                            if (transactionStatusResponse.Response.Result == Result.Success)
                            {
                                Response paymentResponse = transactionStatusResponse.RepeatedMessageResponse.RepeatedResponseMessageBody.PaymentResponse.Response;
                                Console.WriteLine($"Payment result = {paymentResponse.Result.ToString() ?? "Unknown"}, ErrorCondition = {paymentResponse?.ErrorCondition}, Result = {paymentResponse?.AdditionalResponse}");
                                result = paymentResponse.Result is Result.Success or Result.Partial;
                                waitingForResponse = false;
                            }

                            // else check if the transaction is still in progress, and we haven't reached out timeout
                            else if (transactionStatusResponse.Response.ErrorCondition == ErrorCondition.InProgress && timeoutTimer.Elapsed < timeout)
                            {
                                Console.WriteLine("Payment in progress...");
                            }

                            // otherwise, fail
                            else
                            {
                                waitingForResponse = false;
                            }
                        }
                    }
                }
                catch (NetworkException)
                {
                    Console.WriteLine("Waiting for connection...");
                }
                catch (DataMeshGroup.Fusion.TimeoutException)
                {
                    Console.WriteLine("Timeout waiting for result...");
                }
                catch (Exception)
                {
                    Console.WriteLine("Waiting for connection...");
                }

                if (waitingForResponse)
                {
                    System.Threading.Thread.Sleep(requestDelay);
                }

            } while (waitingForResponse);


            return result;
        }
    }

    class Program
    {
        static void Main(string[] args)
        {
            // POS identification provided by DataMesh
            string saleID = args[0]; // Replace with your test SaleId provided by DataMesh
            string poiID = args[1]; // Replace with your test POIID provided by DataMesh
            string kek = "44DACB2A22A4A752ADC1BBFFE6CEFB589451E0FFD83F8B21"; // test environment only - replace for production
            string certificationCode = "98cf9dfc-0db7-4a92-8b8cb66d4d2d7169"; // test environment only - replace for production
            // POS identification provided by POS vendor
            string providerIdentification = "Company A"; // test environment only - replace for production
            string applicationName = "POS Retail"; // test environment only - replace for production
            string softwareVersion = "01.00.00"; // test environment only - replace for production

            // Build logon request
            LoginRequest loginRequest = new LoginRequest(providerIdentification, applicationName, softwareVersion, certificationCode);

            // Build payment request
            decimal amount = 42.00M;
            PaymentRequest paymentRequest = new PaymentRequest(
                transactionID: DateTime.UtcNow.ToString("yyyyMMddhhmmssffff", CultureInfo.InvariantCulture),
                requestedAmount: amount,
                saleItems: new List<SaleItem>()
                {
                    new SaleItem()
                    {
                        ItemID = "1",
                        ProductCode = "1",
                        ProductLabel = "Product",
                        Quantity = 1,
                        UnitOfMeasure = UnitOfMeasure.Other,
                        ItemAmount = amount,
                        UnitPrice = amount
                    }
                });

            try
            {
                // Perform 'events' purchase demo
                bool eventsResult = new PurchaseDemoEvents(saleID, poiID, kek, loginRequest).DoPayment(paymentRequest);
                Console.WriteLine($"Events purchase result: {eventsResult}");
            }
            catch (Exception e)
            {
                Console.WriteLine($"Exception processing payment {e.Message} {e.StackTrace}");
            }
        }
    }
}
