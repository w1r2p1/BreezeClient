﻿using NBitcoin;
using NTumbleBit.Services;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stratis.Bitcoin;
using Stratis.Bitcoin.Features.BlockStore;
using Stratis.Bitcoin.Features.WatchOnlyWallet;
using Stratis.Bitcoin.Features.Wallet;

namespace Breeze.TumbleBit.Client.Services
{
    public class FullNodeWalletEntry
    {
        public uint256 TransactionId
        {
            get; set;
        }
        public int Confirmations
        {
            get; set;
        }
    }

    /// <summary>
    /// Workaround around slow Bitcoin Core RPC. 
    /// We are refreshing the list of received transaction once per block.
    /// </summary>
    public class FullNodeWalletCache
    {
        private readonly IRepository _Repo;
        private FullNode fullNode;
        private IWatchOnlyWalletManager watchOnlyWalletManager;
        public FullNodeWalletCache(IRepository repository, FullNode fullNode, IWatchOnlyWalletManager watchOnlyWalletManager)
        {
            if(repository == null)
                throw new ArgumentNullException("repository");
            if (fullNode == null)
                throw new ArgumentNullException("fullNode");
            if (watchOnlyWalletManager == null)
                throw new ArgumentNullException("watchOnlyWalletManager");

            _Repo = repository;
            this.fullNode = fullNode;
            this.watchOnlyWalletManager = watchOnlyWalletManager;
        }

        volatile uint256 _RefreshedAtBlock;

        public void Refresh(uint256 currentBlock)
        {
            var refreshedAt = _RefreshedAtBlock;
            if(refreshedAt != currentBlock)
            {
                lock(_Transactions)
                {
                    if(refreshedAt != currentBlock)
                    {
                        RefreshBlockCount();
                        _Transactions = ListTransactions(ref _KnownTransactions);
                        _RefreshedAtBlock = currentBlock;
                    }
                }
            }
        }

        int _BlockCount;
        public int BlockCount
        {
            get
            {
                if(_BlockCount == 0)
                {
                    RefreshBlockCount();
                }
                return _BlockCount;
            }
        }

        private void RefreshBlockCount()
        {
            Interlocked.Exchange(ref _BlockCount, this.fullNode.WalletManager.LastBlockHeight());
        }

        public Transaction GetTransaction(uint256 txId)
        {
            var cached = GetCachedTransaction(txId);
            if(cached != null)
                return cached;
            var tx = FetchTransaction(txId);
            if(tx == null)
                return null;
            PutCached(tx);
            return tx;
        }

        ConcurrentDictionary<uint256, Transaction> _TransactionsByTxId = new ConcurrentDictionary<uint256, Transaction>();

        private Transaction FetchTransaction(uint256 txId)
        {
            try
            {
                Transaction trx = this.fullNode.MempoolManager?.InfoAsync(txId)?.Result.Trx;

                if (trx == null)
                    trx = this.fullNode.BlockStoreManager?.BlockRepository?.GetTrxAsync(txId).Result;

                return trx;
            }
            catch(Exception) { return null; }
        }

        public FullNodeWalletEntry[] GetEntries()
        {
            lock(_Transactions)
            {
                return _Transactions.ToArray();
            }
        }

        private void PutCached(Transaction tx)
        {
            tx.CacheHashes();
            _Repo.UpdateOrInsert("CachedTransactions", tx.GetHash().ToString(), tx, (a, b) => b);
            lock(_TransactionsByTxId)
            {
                _TransactionsByTxId.TryAdd(tx.GetHash(), tx);
            }
        }

        private Transaction GetCachedTransaction(uint256 txId)
        {
            Transaction tx = null;
            if(_TransactionsByTxId.TryGetValue(txId, out tx))
            {
                return tx;
            }
            var cached = _Repo.Get<Transaction>("CachedTransactions", txId.ToString());
            if(cached != null)
                _TransactionsByTxId.TryAdd(txId, cached);
            return cached;
        }


        List<FullNodeWalletEntry> _Transactions = new List<FullNodeWalletEntry>();
        HashSet<uint256> _KnownTransactions = new HashSet<uint256>();
        List<FullNodeWalletEntry> ListTransactions(ref HashSet<uint256> knownTransactions)
        {
            List<FullNodeWalletEntry> array = new List<FullNodeWalletEntry>();
            knownTransactions = new HashSet<uint256>();
            var removeFromCache = new HashSet<uint256>(_TransactionsByTxId.Values.Select(tx => tx.GetHash()));
            int count = 100;
            int skip = 0;
            int highestConfirmation = 0;

            // List all transactions, including those in watch-only wallet
            // (zero confirmations are acceptable)

            List<uint256> txIdList = new List<uint256>();

            // First examine watch-only wallet
            var watchOnlyWallet = this.watchOnlyWalletManager.GetWallet();

            foreach (var watchOnlyTx in watchOnlyWallet.Transactions)
            {
                txIdList.Add(new uint256(watchOnlyTx.txid));
            }

            // List transactions in regular wallet
            foreach (var wallet in this.fullNode.WalletManager.Wallets)
            {
                foreach (var walletTx in wallet.GetAllTransactionsByCoinType((CoinType)this.fullNode.Network.Consensus.CoinType))
                {
                    txIdList.Add(walletTx.Id);
                }
            }

            foreach(var txId in txIdList)
            {
                var blockHash = this.fullNode.BlockStoreManager?.BlockRepository?.GetTrxBlockIdAsync(txId).Result;
                var block = this.fullNode.Chain.GetBlock(blockHash);
                var blockHeight = block.Height;
                var tipHeight = this.fullNode.Chain.Tip.Height;
                var confirmations = tipHeight - blockHeight;
                var confCount = Math.Max(0, confirmations);

                var entry = new FullNodeWalletEntry()
                {
                    TransactionId = txId,
                    Confirmations = confCount
                };

                removeFromCache.Remove(txId);
                if (knownTransactions.Add(entry.TransactionId))
                {
                    array.Add(entry);
                }

                // TODO: We don't currently have a way of filtering out high confirmation transactions upfront
            }

            foreach (var remove in removeFromCache)
            {
                Transaction opt;
                _TransactionsByTxId.TryRemove(remove, out opt);
            }
            return array;
        }

        public void ImportTransaction(Transaction transaction, int confirmations)
        {
            PutCached(transaction);
            lock(_Transactions)
            {
                if(_KnownTransactions.Add(transaction.GetHash()))
                {
                    _Transactions.Insert(0,
                        new FullNodeWalletEntry()
                        {
                            Confirmations = confirmations,
                            TransactionId = transaction.GetHash()
                        });
                }
            }
        }
    }
}
