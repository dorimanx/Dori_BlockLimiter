using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BlockLimiter.ProcessHandlers;
using BlockLimiter.Settings;
using BlockLimiter.Utility;
using NLog;
using Sandbox.Definitions;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using Torch.API.Managers;
using Torch.Managers.ChatManager;
using VRage.Game;
using VRageMath;

namespace BlockLimiter.Punishment
{
    public class Punish : ProcessHandlerBase
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private readonly HashSet<MySlimBlock> _blockCache = new HashSet<MySlimBlock>();

        private static Dictionary<MySlimBlock, LimitItem.PunishmentType> _blockPunish =
            new Dictionary<MySlimBlock, LimitItem.PunishmentType>();
        private static bool _firstCheckCompleted;

        public override int GetUpdateResolution()
        {
            return Math.Max(BlockLimiterConfig.Instance.PunishInterval, 1) * 1000;
        }

        public override void Handle()
        {
            if (!BlockLimiterConfig.Instance.EnableLimits) return;
            GridCache.GetBlocks(_blockCache);
            RunPunishment(_blockCache);
            _blockCache.Clear();
        }

        public static void Update()
        {
            if (_blockPunish == null || _blockPunish.Count == 0)
                return;

            var updateDictionary = new Dictionary<MySlimBlock, LimitItem.PunishmentType>();
            var _blockPunishCleanup = new Dictionary<MySlimBlock, LimitItem.PunishmentType>();

            lock (_blockPunish)
            {
                for (int i = 0; i < Math.Min(5, _blockPunish.Count); i++)
                {
                    if (_blockPunish.ElementAt(i).Key == null)
                        continue;

                    var (k, v) = _blockPunish.ElementAt(i);

                    if (k.IsDestroyed || (k.FatBlock != null && (k.FatBlock.Closed || k.FatBlock.MarkedForClose)) || v == LimitItem.PunishmentType.None)
                    {
                        _blockPunishCleanup.Add(k, v);
                        continue;
                    }

                    updateDictionary[k] = v;
                }
            }

            if (_blockPunishCleanup.Count > 0)
            {
                foreach(var (key, value) in _blockPunishCleanup)
                {
                    lock (_blockPunish)
                    {
                        if (_blockPunish.ContainsKey(key))
                            _blockPunish.Remove(key);
                    }
                }
            }

            if (updateDictionary.Count == 0)
                return;

            var chatManager = BlockLimiter.Instance.Torch.CurrentSession?.Managers?.GetManager<ChatManagerServer>();

            foreach (var (block, punishment) in updateDictionary)
            {
                lock (_blockPunish)
                {
                    _blockPunish.Remove(block);
                }

                switch (punishment)
                {
                    case LimitItem.PunishmentType.DeleteBlock:
                        BlockLimiter.Instance.Torch.InvokeAsync(() => { block.CubeGrid?.RemoveBlock(block); });

                        BlockLimiter.Instance.Log.Info($"Removed {block.BlockDefinition} from {block.CubeGrid?.DisplayName}");
                        break;

                    case LimitItem.PunishmentType.ShutOffBlock:
                        if (!(block.FatBlock is MyFunctionalBlock fBlock))
                            continue;

                        Block.KillBlock(fBlock);
                        break;

                    case LimitItem.PunishmentType.Explode:
                        BlockLimiter.Instance.Log.Info($"Destroyed {block.BlockDefinition} from {block.CubeGrid?.DisplayName}");

                        BlockLimiter.Instance.Torch.InvokeAsync(() =>
                        {
                            block.DoDamage(block.BlockDefinition.MaxIntegrity, MyDamageType.Fire);
                        });
                        break;

                    default:
                        continue;
                }

                var ownerSteamId = MySession.Static.Players.TryGetSteamId(block.OwnerId);
                if (ownerSteamId == 0 || !MySession.Static.Players.IsPlayerOnline(block.OwnerId)) return;

                chatManager?.SendMessageAsOther(BlockLimiterConfig.Instance.ServerName,
                    $"Punishing {((MyTerminalBlock)block.FatBlock).CustomName} from {block.CubeGrid?.DisplayName} with {punishment}", Color.Yellow, ownerSteamId);
            }
        }

        public static int RunPunishment(HashSet<MySlimBlock> blocks, List<LimitItem.PunishmentType> punishmentTypes = null)
        {
            var totalBlocksPunished = 0;

            if (blocks.Count == 0 || !BlockLimiterConfig.Instance.EnableLimits)
                return 0;

            var limitItems = BlockLimiterConfig.Instance.AllLimits.Where(item => item.FoundEntities.Count > 0 && item.Punishment != LimitItem.PunishmentType.None).ToList();

            if (limitItems.Count == 0) return 0;

            var punishBlocks = new Dictionary<MySlimBlock, LimitItem.PunishmentType>();

            var tasks = new List<Task>();

            foreach (var item in limitItems)
            {
                if (punishmentTypes != null && !punishmentTypes.Contains(item.Punishment)) continue;

                tasks.Add(Task.Run(() => CheckLimit(item)));
            }

            void CheckLimit(LimitItem limit)
            {
                var idsToRemove = new HashSet<long>();
                var punishCount = 0;

                foreach (var (id, count) in limit.FoundEntities)
                {
                    if (id == 0 || limit.IsExcepted(id))
                    {
                        idsToRemove.Add(id);
                        continue;
                    }

                    if (count <= limit.Limit) continue;

                    foreach (var block in blocks)
                    {
                        if (block == null)
                            continue;

                        if (!limit.IsMatch(block.BlockDefinition) || block.CubeGrid.Projector != null)
                            continue;

                        var defBase = MyDefinitionManager.Static.GetDefinition(block.BlockDefinition.Id);

                        if (defBase != null && !_firstCheckCompleted && !defBase.Context.IsBaseGame) continue;

                        if (Math.Abs(punishCount - count) <= limit.Limit)
                            break;

                        if (limit.IgnoreNpcs)
                        {
                            if (MySession.Static.Players.IdentityIsNpc(block.FatBlock.BuiltBy) ||
                                MySession.Static.Players.IdentityIsNpc(block.FatBlock.OwnerId))
                            {
                                idsToRemove.Add(id);
                                continue;
                            }
                        }

                        //Reverting to old shutoff due to performance issues
                        if (limit.Punishment == LimitItem.PunishmentType.ShutOffBlock &&
                            block.FatBlock is MyFunctionalBlock fBlock && (!fBlock.Enabled ||
                                                                           block.FatBlock.MarkedForClose ||
                                                                           block.FatBlock.Closed))
                        {
                            punishCount++;
                            continue;
                        }

                        var playerSteamId = MySession.Static.Players.TryGetSteamId(id);

                        if (playerSteamId > 0 && !Annoy.AnnoyQueue.ContainsKey(playerSteamId))
                        {
                            Annoy.AnnoyQueue[playerSteamId] = DateTime.Now;
                            break;
                        }

                        if (limit.LimitGrids && block.CubeGrid.EntityId == id)
                        {
                            punishCount++;
                            punishBlocks[block] = limit.Punishment;
                            continue;
                        }

                        if (limit.LimitPlayers)
                        {
                            if (Block.IsOwner(block, id))
                            {
                                punishCount++;
                                punishBlocks[block] = limit.Punishment;
                                continue;
                            }
                        }

                        if (!limit.LimitFaction) continue;

                        var faction = MySession.Static.Factions.TryGetFactionById(id);
                        if (faction == null || block.FatBlock.GetOwnerFactionTag()?.Equals(faction.Tag) == false)
                            continue;

                        punishCount++;
                        punishBlocks[block] = limit.Punishment;
                    }
                }

                foreach (var id in idsToRemove)
                {
                    limit.FoundEntities.Remove(id);
                }
            }

            Task.WaitAll(tasks.ToArray());
            totalBlocksPunished = punishBlocks.Count;
            _firstCheckCompleted = !_firstCheckCompleted;

            if (totalBlocksPunished == 0)
                return totalBlocksPunished;

            Log.Debug($"Punishing {punishBlocks.Count} blocks");

            lock (_blockPunish)
            {
                _blockPunish.Clear();
                _blockPunish = punishBlocks;
            }

            return totalBlocksPunished;
        }
    }
}