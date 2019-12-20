//  Copyright (c) 2018 Demerzel Solutions Limited
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

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Specs.ChainSpecStyle;
using Nethermind.Logging;

namespace Nethermind.AuRa.Validators
{
    public class MultiValidator : IAuRaValidatorProcessor
    {
        private readonly IAuRaAdditionalBlockProcessorFactory _validatorFactory;
        private readonly IBlockTree _blockTree;
        private IBlockFinalizationManager _blockFinalizationManager;
        private readonly IDictionary<long, AuRaParameters.Validator> _validators;
        private readonly ILogger _logger;
        private IAuRaValidatorProcessor _currentValidator;
        private bool _isProducing;
        private long _lastProcessedBlock = 0;
        private readonly bool _immediateTransitions;
        private AuRaParameters.Validator _currentValidatorInfo;

        public MultiValidator(AuRaParameters.Validator validator, AuRaParameters parameters, IAuRaAdditionalBlockProcessorFactory validatorFactory, IBlockTree blockTree, ILogManager logManager)
        {
            if (validator == null) throw new ArgumentNullException(nameof(validator));
            if (validator.ValidatorType != AuRaParameters.ValidatorType.Multi) throw new ArgumentException("Wrong validator type.", nameof(validator));
            _immediateTransitions = (parameters ?? throw new ArgumentNullException(nameof(parameters))).ImmediateTransitions;
            _validatorFactory = validatorFactory ?? throw new ArgumentNullException(nameof(validatorFactory));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            
            _validators = validator.Validators?.Count > 0
                ? validator.Validators
                : throw new ArgumentException("Multi validator cannot be empty.", nameof(validator.Validators));
        }

        private void InitCurrentValidator(long blockNumber)
        {
            EnsureCorrectValidatorsForBlock(blockNumber, out _, true);
            _lastProcessedBlock = blockNumber;
        }

        private bool TryGetLastValidator(long blockNum, out KeyValuePair<long, AuRaParameters.Validator> validator)
        {
            var headNumber = _blockTree.Head?.Number ?? 0;
            
            validator = default;
            bool found = false;
            
            foreach (var kvp in _validators)
            {
                if (kvp.Key <= blockNum || kvp.Key <= headNumber && CanChangeValidatorImmediately(kvp.Value))
                {
                    validator = kvp;
                    found = true;
                }
            }
            
            return found;
        }

        private void OnBlocksFinalized(object sender, FinalizeEventArgs e)
        {
            for (int i = 0; i < e.FinalizedBlocks.Count; i++)
            {
                var finalizedBlockHeader = e.FinalizedBlocks[i];
                if (TryGetValidator(finalizedBlockHeader.Number, out var validator) && !CanChangeValidatorImmediately(validator))
                {
                    SetCurrentValidator(e.FinalizingBlock.Number, validator);
                    if (_logger.IsInfo && !_isProducing) _logger.Info($"Applying chainspec validator change signalled at block {finalizedBlockHeader.ToString(BlockHeader.Format.Short)} at block {e.FinalizingBlock.ToString(BlockHeader.Format.Short)}.");
                }
            }
        }

       
        public void PreProcess(Block block, ProcessingOptions options = ProcessingOptions.None)
        {
            var notProducing = !options.IsProducingBlock();
            if (notProducing)
            {
                EnsureCorrectValidatorsForBlockWhenProcessing(block);
            }

            _currentValidator?.PreProcess(block, options);
        }

        private void EnsureCorrectValidatorsForBlockWhenProcessing(Block block)
        {
            long previousBlockNumber = block.Number - 1;
            bool isNotConsecutive = previousBlockNumber != _lastProcessedBlock;
            if (isNotConsecutive)
            {
                if (!EnsureCorrectValidatorsForBlock(previousBlockNumber, out var validatorInfo))
                {
                    bool canSetValidatorAsCurrent = !TryGetLastValidator(validatorInfo.Key - 1, out var previousValidatorInfo);
                    long? finalizedAtBlockNumber = null;
                    if (!canSetValidatorAsCurrent)
                    {
                        SetCurrentValidator(previousValidatorInfo);
                        finalizedAtBlockNumber = _blockFinalizationManager.GetFinalizedLevel(validatorInfo.Key);
                        canSetValidatorAsCurrent = finalizedAtBlockNumber != null;
                    }

                    if (canSetValidatorAsCurrent)
                    {
                        SetCurrentValidator(finalizedAtBlockNumber ?? validatorInfo.Key, validatorInfo.Value);
                    }
                }
            }
            else if (TryGetValidator(block.Number, out var validator))
            {
                if (CanChangeValidatorImmediately(validator))
                {
                    SetCurrentValidator(block.Number, validator);
                    if (_logger.IsInfo)  _logger.Info($"Immediately applying chainspec validator change signalled at block at block {block.ToString(Block.Format.Short)} to {validator.ValidatorType}.");
                }
                else if (_logger.IsInfo) _logger.Info($"Signal for switch to chainspec {validator.ValidatorType} based validator set at block {block.ToString(Block.Format.Short)}.");
            }
        }
        
        public void EnsureCorrectValidatorsForBlock(long blockNumber)
        {
            EnsureCorrectValidatorsForBlock(blockNumber, out _);
        }

        private bool EnsureCorrectValidatorsForBlock(long blockNumber, out KeyValuePair<long, AuRaParameters.Validator> validatorInfo, bool forceChange = false)
        {
            if (TryGetLastValidator(blockNumber, out validatorInfo))
            {
                if (CanChangeValidatorImmediately(validatorInfo.Value) || forceChange)
                {
                    SetCurrentValidator(validatorInfo);
                    return true;
                }
                else
                {
                    return false;
                }
            }

            return true;
        }

        private bool CanChangeValidatorImmediately(AuRaParameters.Validator validator) => _immediateTransitions || validator.ValidatorType.CanChangeImmediately();

        private bool TryGetValidator(long blockNumber, out AuRaParameters.Validator validator) => _validators.TryGetValue(blockNumber, out validator);
        
        public void PostProcess(Block block, TxReceipt[] receipts, ProcessingOptions options = ProcessingOptions.None)
        {
            _currentValidator?.PostProcess(block, receipts, options);
            _lastProcessedBlock = block.Number;
        }
        
        public bool IsValidSealer(Address address, long step) => _currentValidator?.IsValidSealer(address, step) == true;

        public int MinSealersForFinalization => _currentValidator.MinSealersForFinalization;
        
        public int CurrentSealersCount => _currentValidator.CurrentSealersCount;

        void IAuRaValidator.SetFinalizationManager(IBlockFinalizationManager finalizationManager, bool forProducing)
        {
            if (_blockFinalizationManager != null)
            {
                _blockFinalizationManager.BlocksFinalized -= OnBlocksFinalized;
            }

            _blockFinalizationManager = finalizationManager;
            _isProducing = forProducing;
            
            if (_blockFinalizationManager != null)
            {
                _blockFinalizationManager.BlocksFinalized += OnBlocksFinalized;
                InitCurrentValidator(_blockFinalizationManager.LastFinalizedBlockLevel);
            }

            _currentValidator?.SetFinalizationManager(finalizationManager, forProducing);
        }

        private void SetCurrentValidator(KeyValuePair<long, AuRaParameters.Validator> validatorInfo)
        {
            SetCurrentValidator(validatorInfo.Key, validatorInfo.Value);
        }
        
        private void SetCurrentValidator(long finalizedAtBlockNumber, AuRaParameters.Validator validator)
        {
            if (_currentValidatorInfo != validator)
            {
                _currentValidator?.SetFinalizationManager(null);
                _currentValidator = CreateValidator(finalizedAtBlockNumber, validator);
                _currentValidator.SetFinalizationManager(_blockFinalizationManager, _isProducing);
                _currentValidatorInfo = validator;
            }
        }

        private IAuRaValidatorProcessor CreateValidator(long finalizedAtBlockNumber, AuRaParameters.Validator validator)
        {
            return _validatorFactory.CreateValidatorProcessor(validator, finalizedAtBlockNumber + (_immediateTransitions ? 0 : 1));
        }
    }
}