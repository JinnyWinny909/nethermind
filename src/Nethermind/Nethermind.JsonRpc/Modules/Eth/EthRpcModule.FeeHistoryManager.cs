//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualBasic;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.JsonRpc.Modules.Eth
{
    public partial class EthRpcModule
    {
        public class FeeHistoryManager : IFeeHistoryManager
        {
            private readonly IBlockFinder _blockFinder;
            private readonly IBlockRangeManager _blockRangeManager;

            public FeeHistoryManager(IBlockFinder blockFinder, IBlockRangeManager? blockRangeManager = null)
            {
                _blockFinder = blockFinder;
                _blockRangeManager = blockRangeManager ?? GetBlockRangeManager(_blockFinder);
            }

            protected virtual IBlockRangeManager GetBlockRangeManager(IBlockFinder blockFinder)
            {
                return new BlockRangeManager(blockFinder);
            }
            
            public ResultWrapper<FeeHistoryResult> GetFeeHistory(ref long blockCount, long lastBlockNumber,
                double[]? rewardPercentiles = null)
            {
                long? headBlockNumber = null;
                ResultWrapper<FeeHistoryResult> initialCheckResult = InitialChecksPassed(ref blockCount, rewardPercentiles);
                if (initialCheckResult.Result.ResultType == ResultType.Failure)
                {
                    return initialCheckResult;
                }
                
                int maxHistory = 1;
                ResultWrapper<BlockRangeInfo> blockRangeResult = _blockRangeManager.ResolveBlockRange(ref lastBlockNumber, ref blockCount, maxHistory, ref headBlockNumber);
                if (blockRangeResult.Result.ResultType == ResultType.Failure)
                {
                    return ResultWrapper<FeeHistoryResult>.Fail(blockRangeResult.Result.Error ?? "Error message in ResolveBlockRange not set correctly.");
                }

                BlockRangeInfo blockRangeInfo = blockRangeResult.Data;
                long? oldestBlockNumber = blockRangeInfo.LastBlockNumber + 1 - blockRangeInfo.BlockCount;
                if (oldestBlockNumber == null)
                {
                    string output = StringOfNullElements(blockRangeInfo);
                    return ResultWrapper<FeeHistoryResult>.Fail($"{output} is null");
                }
                
                
                return FeeHistoryLookup(blockCount, lastBlockNumber, rewardPercentiles);
            }

            private static string StringOfNullElements(BlockRangeInfo blockRangeInfo)
            {
                List<string> nullStrings = new();
                if (blockRangeInfo.LastBlockNumber == null)
                    nullStrings.Add("blockRangeInfo.LastBlockNumber");
                if (blockRangeInfo.BlockCount == null)
                    nullStrings.Add("blockRangeInfo.BlockCount");
                string output = Strings.Join(nullStrings.ToArray(), ", ");
                return output;
            }

            private ResultWrapper<FeeHistoryResult> InitialChecksPassed(ref long blockCount, double[] rewardPercentiles)
            {
                if (blockCount < 1)
                {
                    return ResultWrapper<FeeHistoryResult>.Fail($"blockCount: Block count, {blockCount}, is less than 1.");
                }

                if (blockCount > 1024)
                {
                    blockCount = 1024;
                }

                if (rewardPercentiles != null)
                {
                    int index = -1;
                    int count = rewardPercentiles.Length;
                    int[] incorrectlySortedIndexes = rewardPercentiles
                        .Select(val => ++index)
                        .Where(val => index > 0 
                                      && index < count 
                                      && rewardPercentiles[index] < rewardPercentiles[index - 1])
                        .ToArray();
                    if (incorrectlySortedIndexes.Any())
                    {
                        int firstIndex = incorrectlySortedIndexes.ElementAt(0);
                        return ResultWrapper<FeeHistoryResult>.Fail(
                           $"rewardPercentiles: Value at index {firstIndex}: {rewardPercentiles[firstIndex]} is less than " +
                           $"the value at previous index {firstIndex - 1}: {rewardPercentiles[firstIndex - 1]}.");
                    }

                    double[] invalidValues = rewardPercentiles.Select(val => val).Where(val => val < 0 || val > 100)
                        .ToArray();
                    
                    if (invalidValues.Any())
                    {
                        return ResultWrapper<FeeHistoryResult>.Fail(
                            $"rewardPercentiles: Values {String.Join(", ", invalidValues)} are below 0 or greater than 100.");
                    }
                }
                return ResultWrapper<FeeHistoryResult>.Success(new FeeHistoryResult(0,Array.Empty<UInt256[]>(),Array.Empty<UInt256>(),Array.Empty<UInt256>()));
            }

            private ResultWrapper<FeeHistoryResult> FeeHistoryLookup(long blockCount, long lastBlockNumber, double[]? rewardPercentiles = null)
            {
                Block pendingBlock = _blockFinder.FindPendingBlock();
                long firstBlockNumber = Math.Max(lastBlockNumber + 1 - blockCount, 0);
                List<BlockFeeInfo> blockFeeInfos = new();
                for (; firstBlockNumber < lastBlockNumber; firstBlockNumber++)
                {
                    BlockFeeInfo blockFeeInfo = new();
                    if (pendingBlock != null && firstBlockNumber > pendingBlock.Number)
                    {
                        blockFeeInfo.Block = pendingBlock;
                        blockFeeInfo.BlockNumber = pendingBlock.Number;
                    }
                    else
                    {
                        Block block;
                        if (rewardPercentiles != null && rewardPercentiles.Length != 0)
                        {
                            blockFeeInfo.Block = _blockFinder.FindBlock(firstBlockNumber);
                        }
                        else
                        {
                            blockFeeInfo.BlockHeader = _blockFinder.FindHeader(firstBlockNumber);
                        }
                        blockFeeInfo.BlockNumber = firstBlockNumber;
                    }

                    if (blockFeeInfo.Block != null)
                    {
                        blockFeeInfo.BlockHeader = blockFeeInfo.Block.Header;
                    }

                    if (blockFeeInfo.BlockHeader != null)
                    {
                        ProcessBlock(ref blockFeeInfo, rewardPercentiles);
                    }

                    blockFeeInfos.Add(blockFeeInfo);
                }
            }

            private void ProcessBlock(ref BlockFeeInfo blockFeeInfo, double[]? rewardPercentiles)
            {
                blockFeeInfo.BaseFee = blockFeeInfo.BlockHeader?.BaseFeePerGas ?? 0;
                
            }
        }
    }
}
