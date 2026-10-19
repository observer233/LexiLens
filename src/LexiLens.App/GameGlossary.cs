namespace LexiLens.App;

internal static class GameGlossary
{
    // Original, concise learning hints. No third-party dictionary data bundled.
    private static readonly Dictionary<string, string> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        ["quest"] = "任务；冒险任务", ["objective"] = "目标；任务目标", ["relic"] = "遗物；遗迹中的珍宝",
        ["ancient"] = "古老的；古代的", ["enemy"] = "敌人", ["arrives"] = "到达（arrive 的第三人称单数）",
        ["arrive"] = "到达", ["gather"] = "收集；聚集", ["healing"] = "治疗的；恢复生命的",
        ["herb"] = "草药", ["herbs"] = "草药（herb 的复数）", ["abandoned"] = "被遗弃的；废弃的",
        ["tower"] = "塔；高塔", ["inventory"] = "物品栏；背包", ["merchant"] = "商人",
        ["outpost"] = "前哨站；边远据点", ["dawn"] = "黎明；破晓", ["reach"] = "到达；够到",
        ["find"] = "找到；发现", ["before"] = "在……之前", ["near"] = "在……附近",
        ["return"] = "返回；归还", ["full"] = "满的", ["must"] = "必须",
        ["defeat"] = "击败", ["defend"] = "防守；保卫", ["attack"] = "攻击", ["dodge"] = "闪避",
        ["parry"] = "招架；格挡", ["shield"] = "盾牌；护盾", ["sword"] = "剑", ["armor"] = "护甲；盔甲",
        ["armour"] = "护甲；盔甲", ["weapon"] = "武器", ["damage"] = "伤害；损坏", ["critical"] = "关键的；暴击的",
        ["stamina"] = "耐力", ["health"] = "生命值；健康", ["mana"] = "法力值", ["potion"] = "药水",
        ["skill"] = "技能", ["ability"] = "能力；技能", ["cooldown"] = "技能冷却时间", ["upgrade"] = "升级；强化",
        ["unlock"] = "解锁", ["reward"] = "奖励", ["experience"] = "经验；经历", ["level"] = "等级；关卡",
        ["equip"] = "装备；穿戴", ["equipment"] = "装备", ["loot"] = "战利品；搜刮", ["craft"] = "制作；合成",
        ["collect"] = "收集", ["retrieve"] = "取回；找回", ["deliver"] = "送达；交付", ["escort"] = "护送",
        ["survive"] = "幸存；存活", ["escape"] = "逃脱", ["explore"] = "探索", ["investigate"] = "调查",
        ["hidden"] = "隐藏的", ["entrance"] = "入口", ["dungeon"] = "地牢；地下城", ["ruins"] = "废墟；遗迹",
        ["fortress"] = "堡垒；要塞", ["village"] = "村庄", ["camp"] = "营地", ["checkpoint"] = "检查点；存档点",
        ["companion"] = "伙伴；同伴", ["ally"] = "盟友", ["hostile"] = "敌对的", ["neutral"] = "中立的",
        ["rare"] = "稀有的", ["legendary"] = "传说级的；传奇的", ["common"] = "常见的；普通品质的",
        ["durability"] = "耐久度", ["resistance"] = "抗性；抵抗", ["strength"] = "力量", ["agility"] = "敏捷",
        ["intelligence"] = "智力", ["dexterity"] = "灵巧；敏捷", ["stealth"] = "潜行；隐蔽", ["perception"] = "感知",
        ["accept"] = "接受", ["decline"] = "拒绝", ["confirm"] = "确认", ["abandon"] = "放弃；遗弃",
        ["summon"] = "召唤", ["revive"] = "复活；救起", ["respawn"] = "重生；重新出现", ["bonfire"] = "篝火"
    };

    internal static string? Lookup(string word) => Words.GetValueOrDefault(word);
}
