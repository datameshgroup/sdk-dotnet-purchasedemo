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
    public class PurchaseDemoAsync : IDisposable
    {
        private readonly IFusionClient fusionClient;
        
        public PurchaseDemoAsync(string saleID, string poiID, string kek, LoginRequest loginRequest)
        {
            fusionClient = new FusionClient(useTestEnvironment: true)
            {
                SaleID = saleID,
                POIID = poiID,
                KEK = kek,
                LoginRequest = loginRequest
            };
        }

        public void Dispose()
        {
            fusionClient?.Dispose();
        }

        public async Task<bool> DoPayment(PaymentRequest paymentRequest)
        {
            SaleToPOIMessage request = null;
            bool result = false;
            string displayString;
            try
            {
                request = await fusionClient.SendAsync(paymentRequest);

                bool waitingForResponse = true;
                do
                {
                    MessagePayload MessagePayload = await fusionClient.RecvAsync();
                    switch (MessagePayload)
                    {
                        case LoginResponse r: // Autologin must have been sent
                            displayString = $"Auto Login result = {r.Response.Result}";
                            if (r.Response.Result != Result.Success && r.Response.Result != Result.Partial)
                            {
                                displayString += $", ErrorCondition = {r.Response?.ErrorCondition}, Result = {r.Response?.AdditionalResponse}";
                            }
                            Console.WriteLine(displayString);
                            break;

                        case DisplayRequest r:
                            var cashierDisplay = r.GetCashierDisplayAsPlainText();
                            if (cashierDisplay != null)
                            {
                                Console.WriteLine($"CASHIER DISPLAY [{cashierDisplay}]");
                            }
                            break;

                        case PaymentResponse r:
                            // Validate SaleTransactionID
                            if (!r.SaleData.SaleTransactionID.Equals(paymentRequest.SaleData.SaleTransactionID))
                            {
                                Console.WriteLine($"Unknown SaleId {r.SaleData.SaleTransactionID.TransactionID}");
                                break;
                            }

                            displayString = $"Payment result = {r.Response.Result}";
                            if (r.Response.Result != Result.Success && r.Response.Result != Result.Partial)
                            {
                                displayString += $", ErrorCondition = {r.Response?.ErrorCondition}, Result = {r.Response?.AdditionalResponse}";
                            }
                            Console.WriteLine(displayString);

                            var saleReceipt = r.GetReceiptAsPlainText(DocumentQualifier.SaleReceipt);
                            if (saleReceipt != null)
                            {
                                Console.WriteLine($"Sale receipt = {Environment.NewLine}{saleReceipt}");
                            }

                            result = (r.Response.Result == Result.Success) || (r.Response.Result == Result.Partial);
                            waitingForResponse = false;
                            break;
                        default:
                            Console.WriteLine("Unknown result");
                            break;

                    }
                }
                while (waitingForResponse);
            }
            catch (FusionException fe)
            {
                Console.WriteLine($"Exception processing payment {fe.Message} {fe.StackTrace}");
                if (fe.ErrorRecoveryRequired && request != null)
                {
                    // await error handling
                    result = await HandleErrorRecovery(request);
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"Exception processing payment {e.Message} {e.StackTrace}");
            }
            
            return result;
        }

        private async Task<bool> HandleErrorRecovery(SaleToPOIMessage request)
        {
            Console.WriteLine($"Error recovery...");

            bool result = false;

            TimeSpan timeout = TimeSpan.FromSeconds(90);
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
                    TransactionStatusResponse transactionStatusResponse = await fusionClient.SendRecvAsync<TransactionStatusResponse>(transactionStatusRequest);

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
                    await Task.Delay(requestDelay);
                }

            } while (waitingForResponse);


            return result;
        }
    }

    class Program
    {
        private static async Task Main(string[] args)
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
            PaymentRequest paymentRequest = new PaymentRequest(transactionID: DateTime.UtcNow.ToString("yyyyMMddhhmmssffff", CultureInfo.InvariantCulture), requestedAmount: amount);
            paymentRequest.AddSaleItem(productCode: "XXYYZZ123", productLabel: "Name of product", itemAmount: amount);

            try
            {
                // Perform 'async' purchase demo
                bool asyncResult = await new PurchaseDemoAsync(saleID, poiID, kek, loginRequest).DoPayment(paymentRequest);
                Console.WriteLine($"Async purchase result: {asyncResult}");
            }
            catch (Exception e)
            {
                Console.WriteLine($"Exception processing payment {e.Message} {e.StackTrace}");
            }
        }
    }
}
