using System.Text;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
await InventoryDiscardRefreshChecks.RunAsync();
