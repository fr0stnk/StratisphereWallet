using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json;
using Stratis.SmartContracts;
using Unity3dApi;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using Object = UnityEngine.Object;

public class MyCollectionController
{
    public bool CollectionLoadingInProgress { get; private set; }
    public ItemUpdateListener ItemsListener;

    private bool itemsLoaded = false;
    private ConcurrentDictionary<string, NFTItem> storage;

    private List<NFTItem> itemsList
    {
        get => new List<NFTItem>(storage.Values);
    }

    private List<NFTMetadataModel> NFTMetadataModels;

    private string WalletAddress
    {
        get => NFTWallet.Instance.StratisUnityManager.GetAddress().ToString();
    }
    private HttpClient client = new HttpClient();
    private CancellationTokenSource cancellation = new CancellationTokenSource();

    public MyCollectionController()
    {
        storage = new ConcurrentDictionary<string, NFTItem>();
        NFTMetadataModels = new List<NFTMetadataModel>();

        CollectionLoadingInProgress = false;
        client.Timeout = TimeSpan.FromSeconds(10);
    }

    public async UniTask OnShow()
    {
        cancellation = new CancellationTokenSource();

        if (!itemsLoaded)
        {
            await this.LoadItems(cancellation.Token);
        }

        itemsLoaded = true;
    }

    public async UniTask OnHide()
    {
        this.cancellation?.Cancel();

        while (CollectionLoadingInProgress)
            await UniTask.Delay(50);
    }

    public async UniTask LoadItems(CancellationToken token)
    {
        NFTMetadataModels = new List<NFTMetadataModel>(NFTMetadataModels.Count);

        CollectionLoadingInProgress = true;

        await UniTask.SwitchToThreadPool();

        var items = await this.LoadCollectionItemsAsync(token);

        foreach (var item in items)
        {
            this.SaveItemToStorage(item);
        }

        await UniTask.SwitchToMainThread();

        this.ItemsListener.OnItemsLoaded(items);

        await UniTask.SwitchToThreadPool();

        List<UniTask> itemsLoadingTasks = new List<UniTask>();

        foreach (var item in items)
        {
            itemsLoadingTasks.Add(
                UniTask.Defer(async () => await this.LoadItemInfo(item.ContractAddress, item.TokenID, token))
            );
        }

        await UniTask.WhenAll(itemsLoadingTasks);

        LogMetadataModels();

        PreconvertAnimations(
            itemsList
                .Select(item => item.AnimationURL)
                .Where(animationURL => !string.IsNullOrEmpty(animationURL))
                .ToList()
        );
    }

    private void LogMetadataModels()
    {
        var allModels = JsonConvert.SerializeObject(NFTMetadataModels);
        UnityEngine.Debug.Log(allModels);
    }

    public NFTItem GetItem(string contractAddress, long tokenID)
    {
        string key = contractAddress + "#" + tokenID;

        if (storage.ContainsKey(key))
        {
            return storage[key];
        }

        return null;
    }

    private void SaveItemToStorage(NFTItem item)
    {
        string key = item.ContractAddress + "#" + item.TokenID;
        storage[key] = item;
    }

    private async UniTask<List<NFTItem>> LoadCollectionItemsAsync(CancellationToken token)
    {
        List<DeployedNFTModel> knownNfts = NFTWallet.Instance.LoadKnownNfts();
        token.ThrowIfCancellationRequested();

        OwnedNFTsModel myNfts = await NFTWallet.Instance.StratisUnityManager.Client.GetOwnedNftsAsync(this.WalletAddress, token);
        List<NFTItem> items = new List<NFTItem>();

        foreach (var contractAddressToOwnedIds in myNfts.OwnedIDsByContractAddress)
        {
            token.ThrowIfCancellationRequested();

            string contractAddress = contractAddressToOwnedIds.Key;
            List<long> ownedIds = contractAddressToOwnedIds.Value.Distinct().ToList();

            DeployedNFTModel knownNft = knownNfts.FirstOrDefault(x => x.ContractAddress == contractAddress);
            string nftName = (knownNft == null) ? string.Empty : knownNft.NftName;

            foreach (var id in ownedIds)
            {
                items.Add(
                    new NFTItem()
                    {
                        ContractAddress = contractAddress,
                        TokenID = id,
                        Name = string.Format("{0}  ({1})", nftName, id),
                        SellURL = MarketplaceIntegration.Instance.GetSellURI(contractAddress, id),
                        ImageIsLoading = true
                    }
                );
            }
        }

        return items;
    }

    private async UniTask LoadItemInfo(string contractAddress, long tokenID, CancellationToken cancellationToken)
    {
        var item = this.GetItem(contractAddress, tokenID);

        cancellationToken.ThrowIfCancellationRequested();

        await this.LoadItemMetadata(contractAddress, tokenID, cancellationToken);

        await RequestImageLoading(item, cancellationToken);
    }

    private async UniTask LoadItemMetadata(string contractAddress, long tokenID, CancellationToken token)
    {
        var tokenURI = await GetTokenURL(contractAddress, tokenID);

        token.ThrowIfCancellationRequested();

        var metadata = ConvertToNFTMetadata(await this.LoadJson(tokenURI, token));

        NFTMetadataModels.Add(metadata);

        var item = GetItem(contractAddress, tokenID);
        HandleImageURIs(item, metadata);
    }

    private async UniTask HandleImageURIs(NFTItem item, NFTMetadataModel metadata)
    {
        string imageUri = !IsAnimationURI(metadata.Image) ? metadata.Image : null;
        string animationUri = (!string.IsNullOrEmpty(metadata.AnimationUrl))
            ? metadata.AnimationUrl
            : IsAnimationURI(metadata.Image) ? metadata.Image : null;

        item.ImageURL = imageUri;
        item.AnimationURL = animationUri;
    }

    private async UniTask RequestImageLoading(NFTItem item, CancellationToken token)
    {
        if (!string.IsNullOrEmpty(item.ImageURL))
        {
            item.ImageIsLoading = true;
        }

        RequestItemUpdate(item);

        try
        {
            item.ImageTexture = await GetRemoteTextureAsync(item.ImageURL, token);
        }
        catch (OperationCanceledException e)
        {
            throw e;
        }
        catch (Exception e)
        {
            UnityEngine.Debug.Log(e);
        }
        finally
        {
            item.ImageIsLoading = false;
        }

        RequestItemUpdate(item);
    }

    private bool IsAnimationURI(string uri)
    {
        return uri.EndsWith(".gif") ||
            uri.EndsWith(".mp4") ||
            uri.EndsWith(".mov") ||
            uri.EndsWith(".webm") ||
            uri.EndsWith(".webp") ||
            uri.EndsWith(".avi");
    }

    private async UniTask<string> GetTokenURL(string contractAddress, long tokenID)
    {
        //TODO: we can implement Object Pool optimization here
        NFTWrapper wrapper = new NFTWrapper(NFTWallet.Instance.StratisUnityManager, contractAddress);
        return await wrapper.TokenURIAsync((UInt256)tokenID);
    }

    private async UniTask<string> LoadJson(string url, CancellationToken token)
    {
        try
        {
            HttpResponseMessage result = await this.client.GetAsync(url, token);
            result.EnsureSuccessStatusCode();

            return await result.Content.ReadAsStringAsync();
        }
        catch (OperationCanceledException e)
        {
            throw e;
        }
        catch (Exception e)
        {
            UnityEngine.Debug.Log(e);
            return null;
        }
    }

    private NFTMetadataModel ConvertToNFTMetadata(string json)
    {
        var settings = new JsonSerializerSettings();
        settings.DateFormatString = "YYYY-MM-DD";
        settings.ContractResolver = new CustomMetadataResolver();

        NFTMetadataModel model = null;

        if (json == null)
        {
            model = new NFTMetadataModel();
            model.Name = model.Description = model.Image = "[No metadata]";
        }
        else
        {
            model = JsonConvert.DeserializeObject<NFTMetadataModel>(json, settings);
        }

        return model;
    }

    private async UniTask RequestItemUpdate(NFTItem item)
    {
        await UniTask.SwitchToMainThread();

        if (ItemsListener != null)
        {
            ItemsListener.OnItemUpdated(item);
        }

        await UniTask.SwitchToThreadPool();
    }

    private async UniTask<Texture2D> GetRemoteTextureAsync(string url, CancellationToken token, int timeoutSeconds = 15)
    {
        Texture2D texture;

        await UniTask.SwitchToThreadPool();

        using (UnityWebRequest request = UnityWebRequestTexture.GetTexture(url))
        {
            request.timeout = timeoutSeconds;
            await request.SendWebRequest().WithCancellation(token);

            if (request.result != UnityWebRequest.Result.Success)
            {
                await UniTask.SwitchToMainThread();
                UnityEngine.Debug.Log($"{request.error}, URL:{request.url}");
                return null;
            }

            texture = DownloadHandlerTexture.GetContent(request);
        }

        await UniTask.SwitchToMainThread();

        if (texture != null && (texture.width > 600 || texture.height > 600))
        {
            Texture2D resized = texture.ResizeTexture(600, 600);

            Object.Destroy(texture);

            return resized;
        }

        return texture;
    }

    private void PreconvertAnimations(List<string> externalLinks)
    {
        Task.Run(async () =>
        {
            await Task.Delay(1);
            await MediaConverterManager.Instance.Client.RequestLinksConversionAsync(externalLinks);
        });
    }
}

public class NFTItem
{
    public string ID
    {
        get => this.ContractAddress + "#" + this.TokenID;
    }

    public string ContractAddress;
    public long TokenID;
    public string Name;
    public string Description;
    public string SellURL;
    public string ImageURL;
    public string AnimationURL;
    public Texture2D ImageTexture;
    public bool ImageIsLoading;
}

public interface ItemUpdateListener
{
    public UniTask OnItemsLoaded(List<NFTItem> items);
    public UniTask OnItemUpdated(NFTItem item);
}