using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.WindowsAzure.MobileServices;
using Microsoft.WindowsAzure.MobileServices.SQLiteStore;
using Microsoft.WindowsAzure.MobileServices.Sync;

namespace DevDaysTasks
{
    public class AzureService
    {
        MobileServiceClient client;
        IMobileServiceSyncTable<TodoItem> todoTable;

        static readonly Lazy<AzureService> defaultManagerHolder = new Lazy<AzureService>(() => new AzureService());

        AzureService()
        {

        }

        public static AzureService DefaultManager => defaultManagerHolder.Value;

        public async Task Initialize()
        {
            // Check if Sync Context already has been synchronized
            if (client?.SyncContext?.IsInitialized is true)
                return;

            // Initialize Mobile Client
            client = new MobileServiceClient(Constants.ApplicationURL);

            // Initialize local database for syncing 
            var path = Path.Combine(MobileServiceClient.DefaultDatabasePath, Constants.SyncStorePath);
            using (var store = new MobileServiceSQLiteStore(path))
            {
                store.DefineTable<TodoItem>();

                //Initializes the SyncContext using the default IMobileServiceSyncHandler.
                var handler = new MobileServiceSyncHandler();
                await client.SyncContext.InitializeAsync(store, handler).ConfigureAwait(false);
            }

            todoTable = client.GetSyncTable<TodoItem>();
        }

        public async Task<ObservableCollection<TodoItem>> GetTodoItemsAsync(bool syncItems = false)
        {
            try
            {
                // Make sure, the manager has been initialized
                await Initialize().ConfigureAwait(false);

                // Check if synchronization with backend is requested
                if (syncItems)
                {
                    await SyncAsync().ConfigureAwait(false);
                }
                // Get all uncompleted items from the local database
                var items = await todoTable
                    .Where(todoItem => !todoItem.Done)
                    .OrderBy(todoItem => todoItem.Name)
                    .ToEnumerableAsync().ConfigureAwait(false);

                return new ObservableCollection<TodoItem>(items);
            }
            catch (MobileServiceInvalidOperationException msioe)
            {
                Debug.WriteLine(@"Invalid sync operation: {0}", msioe.Message);
                throw;
            }
            catch (Exception e)
            {
                Debug.WriteLine(@"Sync error: {0}", e.Message);
                throw;
            }
        }

        public async Task SaveTaskAsync(TodoItem item)
        {
            // Make sure, the manager has been initialized
            await Initialize().ConfigureAwait(false);

            // Check if item is new or has already been existent by checking its Id
            if (item.Id is null)
            {
                // Insert new item
                await todoTable.InsertAsync(item).ConfigureAwait(false);
            }
            else
            {
                // Update existing item
                await todoTable.UpdateAsync(item).ConfigureAwait(false);
            }
        }


        public async Task SyncAsync()
        {
            ReadOnlyCollection<MobileServiceTableOperationError> syncErrors = null;

            // Make sure, the manager has been initialized
            await Initialize().ConfigureAwait(false);

            try
            {
                await client.SyncContext.PushAsync().ConfigureAwait(false);

                //The first parameter is a query name that is used internally by the client SDK to implement incremental sync.
                //Use a different query name for each unique query in your program
                await todoTable.PullAsync("allTodoItems", todoTable.CreateQuery()).ConfigureAwait(false);
            }
            catch (MobileServicePushFailedException exc)
            {
                if (exc.PushResult != null)
                {
                    syncErrors = exc.PushResult.Errors;
                }
            }

            // Simple error/conflict handling. A real application would handle the various errors like network conditions,
            // server conflicts and others via the IMobileServiceSyncHandler.
            if (syncErrors != null)
            {
                foreach (var error in syncErrors)
                {
                    switch (error.OperationKind)
                    {
                        case MobileServiceTableOperationKind.Update when error.Result != null:
                            await error.CancelAndUpdateItemAsync(error.Result).ConfigureAwait(false);
                            break;
                        case MobileServiceTableOperationKind.Update:
                            break;
                        default:
                            await error.CancelAndDiscardItemAsync().ConfigureAwait(false);
                            break;
                    }

                    Debug.WriteLine(@"Error executing sync operation. Item: {0} ({1}). Operation discarded.", error.TableName, error.Item["id"]);
                }
            }
        }
    }
}
