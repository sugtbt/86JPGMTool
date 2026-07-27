using System;
using System.Collections.Generic;
using System.Linq;
using DfoGmTool.ServerCore.Game.Currency;
using DfoGmTool.ServerCore.Game.Inventory;
using DfoGmTool.ServerCore.Game.Mailbox;
using DfoGmTool.ServerCore.Game.ReviveCoin;
using Microsoft.Data.Sqlite;

namespace DfoGmTool.Services
{
    public sealed partial class GmService
    {
        // 读侧走服务端在线背包模型(InventoryService.LoadFromDb, 离线/诊断允许),
        // 覆盖全部容器和 82B ItemCore 语义, 不再裸读 character_items / 旧 DTO。
        public object ListItems(int characterId, PvfIndexService pvfIndex)
        {
            int accountId;
            if (!TryGetAccountId(characterId, out accountId))
                return Error("角色不存在: " + characterId);

            using (var conn = new SqliteConnection(_config.ConnectionString))
            {
                conn.Open();
                var inventory = GmInventoryStore.Load(conn, characterId, accountId);
                if (inventory == null)
                    return Error("背包加载失败: " + characterId);

                var rentalExpireTimes = _supplementalItemExpiration.LoadRentalExpireTimes(characterId);
                var items = new List<object>();

                // 主背包虚拟槽(金币/复活币/胜点)单独列出, 不可删除
                foreach (var virtualItem in inventory.GetMainVirtualCounts())
                {
                    if (virtualItem.SlotIndex > 2)
                        continue;

                    items.Add(new
                    {
                        container = "主背包",
                        category = "货币",
                        listType = (int)InventoryListType.Main,
                        slot = (int)virtualItem.SlotIndex,
                        templateId = virtualItem.ItemId,
                        name = pvfIndex.ResolveItemName(virtualItem.ItemId),
                        kind = "special",
                        rarity = 0,
                        count = virtualItem.Count,
                        instanceValue = virtualItem.Count,
                        durability = 0,
                        expireTime = 0,
                        supplementalExpiration = (object)null,
                        templateExpiration = CreateTemplateExpiration(pvfIndex, virtualItem.ItemId),
                        seal = 0,
                        deletable = false,
                    });
                }

                AppendCoreItems(items, "主背包", InventoryListType.Main, inventory, pvfIndex, rentalExpireTimes);
                AppendCoreItems(items, "个人仓库", InventoryListType.PersonalCargo, inventory, pvfIndex, rentalExpireTimes);
                AppendCoreItems(items, "账号金库", InventoryListType.AccountCargo, inventory, pvfIndex, rentalExpireTimes);
                AppendCoreItems(items, "穿戴栏", InventoryListType.Equipment, inventory, pvfIndex, rentalExpireTimes);
                AppendCoreItems(items, "时装", InventoryListType.Avatar, inventory, pvfIndex, rentalExpireTimes);
                AppendCoreItems(items, "宠物", InventoryListType.Pet, inventory, pvfIndex, rentalExpireTimes);

                // 晶块是账号级货币(accounts.cube_*), 不在物品主表, 仅展示, 在账号面板调整
                foreach (var cube in CurrencyService.LoadCubeFragments(conn, null, accountId))
                {
                    items.Add(new
                    {
                        container = "主背包",
                        category = "账号晶块",
                        listType = (int)InventoryListType.Main,
                        slot = cube.Slot,
                        templateId = cube.ItemId,
                        name = pvfIndex.ResolveItemName(cube.ItemId),
                        kind = "special",
                        rarity = pvfIndex.ResolveItemRarity(cube.ItemId),
                        count = cube.Count,
                        instanceValue = cube.Count,
                        durability = 0,
                        expireTime = 0,
                        supplementalExpiration = (object)null,
                        templateExpiration = CreateTemplateExpiration(pvfIndex, cube.ItemId),
                        seal = 0,
                        deletable = false,
                    });
                }

                return new { characterId, count = items.Count, items };
            }
        }

        private static void AppendCoreItems(
            List<object> items,
            string container,
            InventoryListType listType,
            InventoryService inventory,
            PvfIndexService pvfIndex,
            IReadOnlyDictionary<int, int> rentalExpireTimes)
        {
            foreach (var pair in inventory.GetItems(listType))
            {
                var slot = pair.Key;
                var core = pair.Value;
                if (core == null || core.IsEmpty)
                    continue;

                // 主背包 0-2 虚拟槽由虚拟槽通道单独展示
                if (listType == InventoryListType.Main && slot <= 2)
                    continue;

                var kind = pvfIndex.ResolveItemKind(core.ItemId);
                var expireTime = core.ExpireTime;
                if (listType == InventoryListType.Avatar
                    && inventory.AvatarDetails.TryGetDetail(core.Uid, out var avatarDetail)
                    && avatarDetail != null)
                {
                    expireTime = avatarDetail.ExpireDate;
                }
                else if (listType == InventoryListType.Pet
                    && inventory.CreatureDetails.TryGetDetail(core.Uid, out var creatureDetail)
                    && creatureDetail != null
                    && creatureDetail.ExpireDate > 0)
                {
                    expireTime = creatureDetail.ExpireDate;
                }

                items.Add(new
                {
                    container,
                    category = listType == InventoryListType.Main ? ResolveMainSegment(slot) : container,
                    listType = (int)listType,
                    slot = (int)slot,
                    templateId = core.ItemId,
                    name = pvfIndex.ResolveItemName(core.ItemId),
                    kind,
                    rarity = pvfIndex.ResolveItemRarity(core.ItemId),
                    count = kind == "equipment" ? 1 : core.Count,
                    instanceValue = core.InstanceValue,
                    durability = (int)core.Durability,
                    expireTime,
                    supplementalExpiration = CreateSupplementalExpiration(rentalExpireTimes, core.ItemId, expireTime),
                    templateExpiration = CreateTemplateExpiration(pvfIndex, core.ItemId),
                    seal = (int)core.SealFlag,
                    deletable = IsDeletable(listType, slot),
                });
            }
        }

        private static object CreateTemplateExpiration(PvfIndexService pvfIndex, int itemTemplateId)
        {
            var expiration = pvfIndex.ResolveItemExpiration(itemTemplateId);
            return new
            {
                known = expiration.IsKnown,
                absoluteExpireTime = expiration.AbsoluteExpirationUnixTime,
                usablePeriodDays = expiration.UsablePeriodDays,
                dailyDeleteItem = expiration.DailyDeleteItem,
                invalid = expiration.HasInvalidDefinition,
            };
        }

        private static object CreateSupplementalExpiration(
            IReadOnlyDictionary<int, int> rentalExpireTimes,
            int itemTemplateId,
            int instanceExpireTime)
        {
            if (instanceExpireTime <= 0
                && rentalExpireTimes != null
                && rentalExpireTimes.TryGetValue(itemTemplateId, out var expireTime)
                && expireTime > 0)
            {
                return new
                {
                    expireTime,
                    source = "rental",
                };
            }

            return null;
        }

        // 货币行(主背包 slot 0-2)删行会打坏钱包; 晶块(354-359)和账号金库是账号共享, 在账号面板管理
        private static bool IsDeletable(InventoryListType listType, int slot)
        {
            if (listType == InventoryListType.AccountCargo)
                return false;
            if (listType == InventoryListType.Main && slot <= 2)
                return false;
            if (listType == InventoryListType.Main && CurrencyService.IsCubeFragmentSlot(slot))
                return false;
            return true;
        }

        // 走服务端 DELETE_ITEM 同款入口(InventoryDeleteService.TryDeleteForClient):
        // 排列锁清理/整删部分删的语义都由服务端代码处理
        public object DeleteItemAt(int characterId, int listType, int slot, int count)
        {
            int accountId;
            if (!TryGetAccountId(characterId, out accountId))
                return Error("角色不存在: " + characterId);

            var list = (InventoryListType)listType;
            if (!IsDeletable(list, slot))
                return Error("该槽位不允许删除(货币行或账号金库)");

            using (var conn = new SqliteConnection(_config.ConnectionString))
            {
                conn.Open();
                var inventory = GmInventoryStore.Load(conn, characterId, accountId);
                if (inventory == null)
                    return Error("背包加载失败");

                InventoryMutationResult result;
                if (!InventoryDeleteService.TryDeleteForClient(
                        inventory, list, (short)slot, count, out result))
                    return Error("删除失败(槽位为空或该列表不支持删除)");

                if (!GmInventoryStore.Save(conn, characterId, inventory))
                    return Error("背包保存失败");

                return new
                {
                    success = true,
                    characterId,
                    listType,
                    slot,
                    remaining = result != null ? result.RemainingStackCount : 0,
                };
            }
        }

        public object BatchDeleteItems(int characterId, List<BatchDeleteEntry> entries)
        {
            if (entries == null || entries.Count == 0)
                return Error("没有要删除的条目");

            int accountId;
            if (!TryGetAccountId(characterId, out accountId))
                return Error("角色不存在: " + characterId);

            var deleted = 0;
            var failed = new List<object>();
            using (var conn = new SqliteConnection(_config.ConnectionString))
            {
                conn.Open();
                var inventory = GmInventoryStore.Load(conn, characterId, accountId);
                if (inventory == null)
                    return Error("背包加载失败");

                foreach (var entry in entries)
                {
                    var list = (InventoryListType)entry.ListType;
                    if (!IsDeletable(list, entry.Slot))
                    {
                        failed.Add(new { entry.ListType, entry.Slot, reason = "受保护槽位" });
                        continue;
                    }

                    InventoryMutationResult result;
                    if (InventoryDeleteService.TryDeleteForClient(
                            inventory, list, (short)entry.Slot, 0, out result))
                        deleted++;
                    else
                        failed.Add(new { entry.ListType, entry.Slot, reason = "删除失败" });
                }

                if (!GmInventoryStore.Save(conn, characterId, inventory))
                    return Error("背包保存失败");
            }

            return new { success = true, characterId, deleted, failedCount = failed.Count, failed };
        }

        // 主背包 slot 分段, 与服务端 ItemMetadataResolver.GetSlotRange / 各 Slot 常量一致
        private static string ResolveMainSegment(int slot)
        {
            if (slot <= 2) return "货币";        // 0金币 1复活币 2胜点
            if (slot <= 8) return "快捷栏";      // QuickSlot 3-8
            if (slot <= 64) return "装备";       // 9-64 (含租赁)
            if (slot <= 120) return "消耗品";    // 65-120
            if (slot <= 176) return "材料";      // 121-176
            if (slot <= 232) return "任务品";    // 177-232
            if (slot <= 288) return "副职业材料"; // 233-288
            if (slot <= 344) return "徽章";      // 289-344
            if (slot <= 353) return "特殊材料";   // 345-353
            if (slot <= 359) return "账号晶块";   // 354-359 账号共享(accounts表列), 在账号面板调整
            return "其他";
        }

        // GM 系统邮件发件人固定 ID(正数即可, sender 无 FK; 收件箱显示发件人名 "GM")
        private const int GmMailSenderCharacterId = 1999999999;

        // 默认经游戏内邮件发放: 物品走服务端 SendSystemMail 落邮件表,
        // 领取由服务端自身 handler 完成——在线角色也能安全收, 不再直写背包
        // (在线角色的背包真源在服务端内存, 直改 DB 会被内存态覆盖)。
        // direct=true 退居旧的直写背包路径, 仅用于离线角色维护。
        public object GiveItem(int characterId, int itemTemplateId, int count, PvfIndexService pvfIndex, bool direct = false)
        {
            if (itemTemplateId <= 0)
                return Error("itemTemplateId 无效");
            if (count <= 0)
                return Error("数量必须大于 0");

            int accountId;
            if (!TryGetAccountId(characterId, out accountId))
                return Error("角色不存在: " + characterId);

            // 名字解析不到通常意味着 ID 不存在, 直接发下去客户端会异常, 先拦住
            var name = pvfIndex.ResolveItemName(itemTemplateId);
            if (name == null && pvfIndex.IsReady)
                return Error("物品 ID " + itemTemplateId + " 在 PVF 中不存在(装备/堆叠表都没有)");

            // 晶块是账号级货币, 走 accounts.cube_* 字段
            if (CurrencyService.IsCubeFragment(itemTemplateId))
            {
                using (var conn = new SqliteConnection(_config.ConnectionString))
                {
                    conn.Open();
                    using (var tx = conn.BeginTransaction())
                    {
                        CurrencyService.AddCubeFragment(conn, tx, accountId, itemTemplateId, count);
                        tx.Commit();
                    }
                }
                return new { success = true, characterId, itemTemplateId, name, count, slot = CurrencyService.GetCubeFragmentSlot(itemTemplateId) };
            }

            // 复活币走主背包 1 号虚拟槽
            if (ReviveCoinService.IsReviveCoinReward(itemTemplateId))
            {
                using (var conn = new SqliteConnection(_config.ConnectionString))
                {
                    conn.Open();
                    using (var tx = conn.BeginTransaction())
                    {
                        InventoryMainVirtualCountRepository.GrantCurrency(
                            conn, tx, characterId, ReviveCoinService.WalletSlot, count, int.MaxValue);
                        tx.Commit();
                    }
                }
                return new { success = true, characterId, itemTemplateId, name, count, slot = (int)ReviveCoinService.WalletSlot };
            }

            if (direct)
                return GiveItemDirect(characterId, accountId, itemTemplateId, count, name);

            return GiveItemViaMail(characterId, accountId, itemTemplateId, count, name);
        }

        private object GiveItemViaMail(int characterId, int accountId, int itemTemplateId, int count, string name)
        {
            string receiverName = null;
            int receiverLevel = 0;
            using (var conn = new SqliteConnection(_config.ConnectionString))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT name, level FROM characters WHERE character_id = @cid AND delete_flag = 0;";
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    using (var reader = cmd.ExecuteReader())
                    {
                        if (!reader.Read())
                            return Error("角色不存在或已删除: " + characterId);
                        receiverName = reader.GetString(0);
                        receiverLevel = reader.GetInt32(1);
                    }
                }
            }

            var request = new MailboxSendRequest
            {
                SenderCharacterId = GmMailSenderCharacterId,
                SenderAccountId = 0,
                SenderName = "GM",
                SenderLevel = 86,
                ReceiverCharacterId = characterId,
                ReceiverAccountId = accountId,
                ReceiverName = receiverName ?? string.Empty,
                ReceiverLevel = receiverLevel,
                Gold = 0,
                Text = "GM 发放",
                MailType = 1,
                SourceProtocol = 0,
                Unlimited = true,
                IdempotencyKey = "gm:" + Guid.NewGuid().ToString("N"),
                AuditActor = "DfoGmTool",
                AuditReason = "GM 发放",
                Attachments = new[]
                {
                    new MailboxSendAttachmentRequest
                    {
                        ItemId = itemTemplateId,
                        ItemCount = count,
                    },
                },
            };

            var result = _mailboxRepository.SendSystemMail(request);
            if (!result.Success)
                return Error("邮件发放失败: " + MailErrorText(result.Error));

            return new
            {
                success = true,
                characterId,
                itemTemplateId,
                name,
                count,
                viaMail = true,
                messageId = result.MessageId,
            };
        }

        private object GiveItemDirect(int characterId, int accountId, int itemTemplateId, int count, string name)
        {
            var grant = CharacterItemGrantService.TryGrant(
                _config.ConnectionString, characterId, accountId, itemTemplateId, count);
            if (!grant.Success)
                return Error(grant.Error ?? "发放失败(背包可能已满)");

            return new
            {
                success = true,
                characterId,
                itemTemplateId,
                name,
                count = grant.GrantedCount,
                slot = (int)grant.AssignedSlot,
                expireTime = grant.ExpireTime,
                slots = grant.AffectedSlots,
            };
        }

        private static string MailErrorText(MailboxSendError error)
        {
            switch (error)
            {
                case MailboxSendError.None: return "未知错误";
                case MailboxSendError.InvalidRequest: return "请求无效";
                case MailboxSendError.ReceiverNotFound: return "收件角色不存在";
                case MailboxSendError.ReceiverDeleted: return "收件角色已删除";
                case MailboxSendError.InvalidAttachment: return "附件无效(物品不可邮或创建失败)";
                case MailboxSendError.TooManyAttachments: return "附件数量超限";
                case MailboxSendError.NotTradable: return "该物品不可交易";
                case MailboxSendError.AccountBound: return "该物品为账号绑定";
                default: return error.ToString();
            }
        }

        public object RemoveItem(int characterId, int itemTemplateId, int count)
        {
            if (count <= 0)
                count = 1;

            int accountId;
            if (!TryGetAccountId(characterId, out accountId))
                return Error("角色不存在: " + characterId);

            using (var conn = new SqliteConnection(_config.ConnectionString))
            {
                conn.Open();
                var inventory = GmInventoryStore.Load(conn, characterId, accountId);
                if (inventory == null)
                    return Error("背包加载失败");

                InventoryMainItemConsumeResult result;
                if (!inventory.TryConsumeMainItem(itemTemplateId, count, out result)
                    || !result.Success)
                    return Error("移除失败(角色没有该物品或数量不足)");

                if (!GmInventoryStore.Save(conn, characterId, inventory))
                    return Error("背包保存失败");

                var slot = result.Changes.Slots.Count > 0 ? (int)result.Changes.Slots[0].SlotIndex : -1;
                return new
                {
                    success = true,
                    characterId,
                    itemTemplateId,
                    count,
                    slot,
                    remaining = inventory.CountMainItem(itemTemplateId),
                };
            }
        }

        public object AdjustGold(int characterId, int amount)
        {
            if (amount == 0)
                return Error("amount 不能为 0");

            int accountId;
            if (!TryGetAccountId(characterId, out accountId))
                return Error("角色不存在: " + characterId);

            using (var conn = new SqliteConnection(_config.ConnectionString))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    if (amount > 0)
                    {
                        CurrencyService.GrantGold(conn, tx, characterId, amount);
                    }
                    else if (!CurrencyService.TrySpendGold(conn, tx, characterId, -amount))
                    {
                        return Error("扣款失败(金币不足)");
                    }

                    tx.Commit();
                }

                var wallet = CurrencyService.LoadWallet(conn, null, characterId);
                return new { success = true, characterId, amount, gold = wallet.Gold };
            }
        }

        // 三种角色货币覆写: 金币走 CurrencyService 按差额加扣;
        // 复活币(slot1)/胜点(slot2)是虚拟槽, 走服务端虚拟槽仓储同语义直写
        public object SetWalletValue(int characterId, string type, int value)
        {
            if (value < 0)
                return Error("数值不能为负");

            int accountId;
            if (!TryGetAccountId(characterId, out accountId))
                return Error("角色不存在: " + characterId);

            type = (type ?? string.Empty).Trim().ToLowerInvariant();

            using (var conn = new SqliteConnection(_config.ConnectionString))
            {
                conn.Open();

                if (type == "gold")
                {
                    using (var tx = conn.BeginTransaction())
                    {
                        var wallet = CurrencyService.LoadWallet(conn, tx, characterId);
                        var delta = value - wallet.Gold;
                        if (delta > 0)
                            CurrencyService.GrantGold(conn, tx, characterId, delta);
                        else if (delta < 0 && !CurrencyService.TrySpendGold(conn, tx, characterId, -delta))
                            return Error("扣减失败");
                        tx.Commit();
                    }
                    return new { success = true, characterId, type, value };
                }

                short slot;
                switch (type)
                {
                    case "revive": slot = 1; break;
                    case "sp": slot = 2; break;
                    default: return Error("不支持的类型: " + type + " (可用: gold/revive/sp)");
                }

                using (var tx = conn.BeginTransaction())
                {
                    InventoryMainVirtualCountRepository.UpsertCurrencySlot(
                        conn, tx, characterId, slot, value);
                    tx.Commit();
                }
            }
            return new { success = true, characterId, type, value };
        }

        // 点券是账号级余额, 服务端接口按角色定位账号
        public object AdjustCera(int characterId, int amount, string type)
        {
            if (amount == 0)
                return Error("amount 不能为 0");

            int accountId;
            if (!TryGetAccountId(characterId, out accountId))
                return Error("角色不存在: " + characterId);

            var useToken = string.Equals(type, "token", StringComparison.OrdinalIgnoreCase);
            using (var conn = new SqliteConnection(_config.ConnectionString))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    if (amount > 0)
                    {
                        if (useToken)
                            CurrencyService.GrantTokenCera(conn, tx, characterId, amount);
                        else
                            CurrencyService.GrantCera(conn, tx, characterId, amount);
                    }
                    else
                    {
                        var ok = useToken
                            ? CurrencyService.TrySpendTokenCera(conn, tx, characterId, -amount)
                            : CurrencyService.TrySpendCera(conn, tx, characterId, -amount);
                        if (!ok)
                            return Error("扣减失败(余额不足)");
                    }

                    tx.Commit();
                }

                var wallet = CurrencyService.LoadWallet(conn, null, characterId);
                return new { success = true, characterId, accountId, amount, cera = wallet.Cera, tokenCera = wallet.TokenCera };
            }
        }
    }

    public sealed class BatchDeleteEntry
    {
        public int ListType { get; set; }
        public int Slot { get; set; }
    }
}
