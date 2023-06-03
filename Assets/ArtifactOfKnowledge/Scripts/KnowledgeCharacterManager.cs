﻿using RoR2;
using RoR2.UI;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using UnityEngine;
using UnityEngine.Networking;
using XpGainMode = ThinkInvisible.ArtifactOfKnowledge.ArtifactOfKnowledgePlugin.XpScalingConfig.XpGainMode;

namespace ThinkInvisible.ArtifactOfKnowledge {
    public class KnowledgeCharacterManagerModule : TILER2.T2Module<KnowledgeCharacterManagerModule> {

        public override bool managedEnable => false;

        public GameObject managerPrefab { get; private set; }

        public override void SetupAttributes() {
            base.SetupAttributes();

            managerPrefab = ArtifactOfKnowledgePlugin.resources.LoadAsset<GameObject>("Assets/ArtifactOfKnowledge/Prefabs/KnowledgeCharacterManager.prefab");
            R2API.PrefabAPI.RegisterNetworkPrefab(managerPrefab);
        }
    }

    public class KnowledgeCharacterManager : NetworkBehaviour {
        static List<KnowledgeCharacterManager> _instances = new List<KnowledgeCharacterManager>();
        public static readonly ReadOnlyCollection<KnowledgeCharacterManager> readOnlyInstances = _instances.AsReadOnly();
        static Dictionary<GameObject, KnowledgeCharacterManager> _instancesByTarget = new Dictionary<GameObject, KnowledgeCharacterManager>();
        public static readonly ReadOnlyDictionary<GameObject, KnowledgeCharacterManager> readOnlyInstancesByTarget = new ReadOnlyDictionary<GameObject, KnowledgeCharacterManager>(_instancesByTarget);

        [SyncVar]
        public int spentUpgrades = 0;
        [SyncVar]
        public int unspentUpgrades = 0;
        [SyncVar]
        public int rerolls = 0;
        [SyncVar]
        public ulong xp = 0;
        [SyncVar]
        public ulong nextLevelXp = (ulong)ArtifactOfKnowledgePlugin.xpScalingConfig.StartingXp;
        [SyncVar]
        public ulong thisLevelXp = 0;

        [SyncVar]
        internal GameObject targetMasterObject = null;

        GameObject currentUpgradePanel = null;
        HUD currentHud = null;
        KnowledgeXpBar currentXpBar = null;

        public delegate void ModifyMaxOfAnyTierEventHandler(KnowledgeCharacterManager sender, Dictionary<ItemTier[], int> maxOfAnyTier);
        public delegate void ModifyGuaranteedOfAnyTagEventHandler(KnowledgeCharacterManager sender, Dictionary<ItemTag[], (Color borderColor, int remaining)> maxOfAnyTier);
        public delegate void ModifyItemTierWeightsEventHandler(KnowledgeCharacterManager sender, Dictionary<ItemTier, float> tierWeights);
        public delegate void ModifyItemSuperSelectionEventHandler(KnowledgeCharacterManager sender, List<WeightedSelection<PickupIndex>.ChoiceInfo> superSelection);
        public static event ModifyMaxOfAnyTierEventHandler ModifyMaxOfAnyTier;
        public static event ModifyGuaranteedOfAnyTagEventHandler ModifyGuaranteedOfAnyTag;
        public static event ModifyItemTierWeightsEventHandler ModifyItemTierWeights;
        public static event ModifyItemSuperSelectionEventHandler ModifyItemSuperSelection;

        const int SAFETY_LEVEL_CAP = 9001;
        float xpStopwatch = 1f;

        public enum UpgradeActionCode {
            Select, Reroll, Banish
        }

        internal List<(PickupIndex index, Color borderColor)> currentSelection = new List<(PickupIndex index, Color borderColor)>();
        internal HashSet<PickupIndex> banished = new HashSet<PickupIndex>();

        #region Unity Methods
        void Awake() {
            _instances.Add(this);
        }

        void OnDestroy() {
            _instances.Remove(this);
            if(targetMasterObject)
                _instancesByTarget.Remove(targetMasterObject);
        }

        void Update() {
            if(NetworkClient.active && Util.HasEffectiveAuthority(gameObject)) {
                if(!currentHud) ClientDiscoverHud();
                ClientUpdateXpBar();
                if(ArtifactOfKnowledgePlugin.clientConfig.KeybindShowMenu.IsPressed())
                    ClientShowUpgradePanel();
            }
        }

        void FixedUpdate() {
            if(NetworkServer.active && (ArtifactOfKnowledgePlugin.xpScalingConfig.XpMode == XpGainMode.TimeExponential || ArtifactOfKnowledgePlugin.xpScalingConfig.XpMode == XpGainMode.TimeLinear)) {
                xpStopwatch -= Time.fixedDeltaTime;
                if(xpStopwatch <= 0f) {
                    xpStopwatch += 1f;
                    ServerAddXp(1u);
                }
            }
        }
        #endregion

        #region Server
        [Server]
        public void ServerAssignAndStart(GameObject targetMasterObject) {
            if(!targetMasterObject) return;
            this.targetMasterObject = targetMasterObject;
            foreach(var existing in _instancesByTarget.Where(kvp => kvp.Value == this).ToArray()) {
                _instancesByTarget.Remove(existing.Key);
            }
            _instancesByTarget[targetMasterObject] = this;

            ServerGenerateSelection();
        }

        [Server]
        public void ServerAddXp(ulong amount) {
            if(!targetMasterObject) return;
            xp += amount;
            bool changedLevel = false;
            while(xp > nextLevelXp && (unspentUpgrades + spentUpgrades) < SAFETY_LEVEL_CAP) {
                changedLevel = true;
                unspentUpgrades++;
                thisLevelXp = nextLevelXp;
                switch(ArtifactOfKnowledgePlugin.xpScalingConfig.XpMode) {
                    case XpGainMode.Vanilla:
                        nextLevelXp = (ulong)TeamManager.InitialCalcExperience(unspentUpgrades + spentUpgrades + 2, ArtifactOfKnowledgePlugin.xpScalingConfig.StartingXp, ArtifactOfKnowledgePlugin.xpScalingConfig.XpScaling);
                        break;
                    case XpGainMode.KillsExponential:
                    case XpGainMode.TimeExponential:
                        nextLevelXp = (ulong)(ArtifactOfKnowledgePlugin.xpScalingConfig.StartingXp * Mathf.Pow(ArtifactOfKnowledgePlugin.xpScalingConfig.XpScaling, unspentUpgrades + spentUpgrades));
                        break;
                    case XpGainMode.KillsLinear:
                    case XpGainMode.TimeLinear:
                        nextLevelXp = (ulong)(ArtifactOfKnowledgePlugin.xpScalingConfig.StartingXp
                            * (1f + (ArtifactOfKnowledgePlugin.xpScalingConfig.XpScaling
                                    * (unspentUpgrades + spentUpgrades))));
                        break;
                }
                
            }
            if(changedLevel)
                RpcLevelUpEvent();

            RpcForceUpdateUI(changedLevel);
        }

        [Server]
        public void ServerGrantRerolls(int amount) {
            rerolls += amount;

            RpcForceUpdateUI(true);
        }

        [Command]
        public void CmdBanish(int index) {
            if(!NetworkServer.active || !targetMasterObject) return;
            if(rerolls < ArtifactOfKnowledgePlugin.serverConfig.BanishCost || banished.Contains(currentSelection[index].index)) {
                RpcDisplayError(UpgradeActionCode.Banish);
                return;
            }
            rerolls -= ArtifactOfKnowledgePlugin.serverConfig.BanishCost;
            banished.Add(currentSelection[index].index);

            ServerGenerateSelection();
        }

        [Command]
        public void CmdSelect(int index) {
            if(!NetworkServer.active || !targetMasterObject) return;
            if(unspentUpgrades == 0) {
                RpcDisplayError(UpgradeActionCode.Select);
                return;
            }
            var selectedPickup = currentSelection[index];
            var pdef = PickupCatalog.GetPickupDef(selectedPickup.index);

            var inv = targetMasterObject.GetComponent<Inventory>();

            if(pdef.itemIndex != ItemIndex.None) {
                inv.GiveItem(pdef.itemIndex);
            } else if(pdef.equipmentIndex != EquipmentIndex.None) {
                inv.SetEquipmentIndex(pdef.equipmentIndex);
            }

            unspentUpgrades--;
            spentUpgrades++;

            if(unspentUpgrades == 0)
                RpcRemoteCloseUpgradePanel();

            ServerGenerateSelection();
        }

        [Command]
        public void CmdReroll() {
            if(!NetworkServer.active || !targetMasterObject) return;
            if(rerolls == 0) {
                RpcDisplayError(UpgradeActionCode.Reroll);
                return;
            }
            rerolls--;

            ServerGenerateSelection();
        }
        #endregion

        #region Server Selection Generation
        public List<WeightedSelection<PickupIndex>.ChoiceInfo> GenerateItemsSuperSelection() {
            var retv = new List<WeightedSelection<PickupIndex>.ChoiceInfo>();

            Dictionary<ItemTier, float> tierWeights = new Dictionary<ItemTier, float>();

            bool upgradeUncommon = (spentUpgrades % ArtifactOfKnowledgePlugin.itemSelectionConfig.UncommonLevelInterval) == (ArtifactOfKnowledgePlugin.itemSelectionConfig.UncommonLevelInterval - 1);
            bool upgradeRare = (spentUpgrades % ArtifactOfKnowledgePlugin.itemSelectionConfig.RareLevelInterval) == (ArtifactOfKnowledgePlugin.itemSelectionConfig.RareLevelInterval - 1);

            if(upgradeRare) { //TODO: migrate this to a standalone module using ModifyItemTierWeights
                tierWeights[ItemTier.Tier1] = 0f;
                tierWeights[ItemTier.Tier2] = 0f;
                tierWeights[ItemTier.Tier3] = ArtifactOfKnowledgePlugin.itemSelectionConfig.BaseT1Chance;
                tierWeights[ItemTier.VoidTier1] = 0f;
                tierWeights[ItemTier.VoidTier2] = 0f;
                tierWeights[ItemTier.VoidTier3] = ArtifactOfKnowledgePlugin.itemSelectionConfig.BaseT1Chance * ArtifactOfKnowledgePlugin.itemSelectionConfig.BaseVoidChance;
            } else if(upgradeUncommon) {
                tierWeights[ItemTier.Tier1] = 0f;
                tierWeights[ItemTier.Tier2] = ArtifactOfKnowledgePlugin.itemSelectionConfig.BaseT1Chance;
                tierWeights[ItemTier.Tier3] = 0f;
                tierWeights[ItemTier.VoidTier1] = 0f;
                tierWeights[ItemTier.VoidTier2] = ArtifactOfKnowledgePlugin.itemSelectionConfig.BaseT1Chance * ArtifactOfKnowledgePlugin.itemSelectionConfig.BaseVoidChance;
                tierWeights[ItemTier.VoidTier3] = 0f;
            } else {
                tierWeights[ItemTier.Tier1] = ArtifactOfKnowledgePlugin.itemSelectionConfig.BaseT1Chance;
                tierWeights[ItemTier.Tier2] = ArtifactOfKnowledgePlugin.itemSelectionConfig.BaseT2Chance;
                tierWeights[ItemTier.Tier3] = ArtifactOfKnowledgePlugin.itemSelectionConfig.BaseT3Chance;
                tierWeights[ItemTier.VoidTier1] = ArtifactOfKnowledgePlugin.itemSelectionConfig.BaseT1Chance * ArtifactOfKnowledgePlugin.itemSelectionConfig.BaseVoidChance;
                tierWeights[ItemTier.VoidTier2] = ArtifactOfKnowledgePlugin.itemSelectionConfig.BaseT2Chance * ArtifactOfKnowledgePlugin.itemSelectionConfig.BaseVoidChance;
                tierWeights[ItemTier.VoidTier3] = ArtifactOfKnowledgePlugin.itemSelectionConfig.BaseT3Chance * ArtifactOfKnowledgePlugin.itemSelectionConfig.BaseVoidChance;
            }

            tierWeights[ItemTier.Lunar] = ArtifactOfKnowledgePlugin.itemSelectionConfig.BaseLunarChance;

            ModifyItemTierWeights?.Invoke(this, tierWeights);

            foreach(var idef in ItemCatalog.allItemDefs) {
                if(!idef || idef.itemIndex == ItemIndex.None || !tierWeights.ContainsKey(idef.tier) || !Run.instance.IsItemAvailable(idef.itemIndex)) continue;
                if(idef.hidden || idef.ContainsTag(ItemTag.WorldUnique)) continue;
                retv.Add(new WeightedSelection<PickupIndex>.ChoiceInfo {
                    value = PickupCatalog.FindPickupIndex(idef.itemIndex),
                    weight = tierWeights[idef.tier]
                });
            }

            return retv;
        }

        public WeightedSelection<PickupIndex> GenerateGearSuperSelection() {
            var retv = new WeightedSelection<PickupIndex>();

            PickupIndex currentEquipment = (targetMasterObject && targetMasterObject.TryGetComponent<Inventory>(out var tmi) && tmi.currentEquipmentIndex != EquipmentIndex.None) ? PickupCatalog.FindPickupIndex(tmi.currentEquipmentIndex) : PickupIndex.none;

            foreach(var drop in Run.instance.availableEquipmentDropList) {
                if(banished.Contains(drop)) continue;
                if(drop == currentEquipment) continue;
                retv.AddChoice(drop, ArtifactOfKnowledgePlugin.itemSelectionConfig.BaseEquipChance);
            }

            foreach(var drop in Run.instance.availableLunarEquipmentDropList) {
                if(banished.Contains(drop)) continue;
                if(drop == currentEquipment) continue;
                retv.AddChoice(drop, ArtifactOfKnowledgePlugin.itemSelectionConfig.BaseLunarEquipChance);
            }

            return retv;
        }

        [Server]
        public void ServerGenerateSelection() {
            var newSuperSelection = GenerateItemsSuperSelection();
            var newGearSuperSelection = GenerateGearSuperSelection();
            var subSelection = new WeightedSelection<PickupIndex>();

            currentSelection.Clear();

            Dictionary<ItemTier[], int> maxOfAnyTier = new Dictionary<ItemTier[], int>();
            Dictionary<ItemTag[], (Color borderColor, int remaining)> guaranteedOfAnyTag = new Dictionary<ItemTag[], (Color borderColor, int remaining)>(); //todo: prevent selection of items which grant more of these once equal to total selections

            maxOfAnyTier[new[] { ItemTier.Lunar }] = ArtifactOfKnowledgePlugin.itemSelectionConfig.BaseMaxLunar;
            maxOfAnyTier[new[] { ItemTier.VoidTier1, ItemTier.VoidTier2, ItemTier.VoidTier3 }] = ArtifactOfKnowledgePlugin.itemSelectionConfig.BaseMaxVoid;

            int selectionSize = ArtifactOfKnowledgePlugin.itemSelectionConfig.SelectionSize;

            if(ArtifactOfKnowledgePlugin.itemSelectionConfig.GuaranteeCategories) {
                guaranteedOfAnyTag[new[] { ItemTag.Damage }] = (new Color(1f, 0.2f, 0.2f), 1);
                guaranteedOfAnyTag[new[] { ItemTag.Utility }] = (new Color(0.2f, 0.2f, 1f), 1);
                guaranteedOfAnyTag[new[] { ItemTag.Healing }] = (new Color(0.2f, 1f, 0.2f), 1);
            }

            ModifyMaxOfAnyTier?.Invoke(this, maxOfAnyTier);
            ModifyGuaranteedOfAnyTag?.Invoke(this, guaranteedOfAnyTag);
            ModifyItemSuperSelection?.Invoke(this, newSuperSelection);

            for(int i = 0; i < selectionSize; i++) {
                subSelection.Clear();

                IEnumerable<WeightedSelection<PickupIndex>.ChoiceInfo> filteredChoices;

                //Restrict to guaranteed tags, if any are left
                var remainingGuaranteedTags = guaranteedOfAnyTag.Where(kvp => kvp.Value.remaining > 0).SelectMany(kvp => kvp.Key);
                if(remainingGuaranteedTags.Count() > 0) {
                    filteredChoices = newSuperSelection.Where(c => {
                        var lpdef = PickupCatalog.GetPickupDef(c.value);
                        var lidef = ItemCatalog.GetItemDef(lpdef.itemIndex);
                        if(!lidef) return true;
                        return remainingGuaranteedTags.Intersect(lidef.tags).Count() > 0;
                    });
                } else filteredChoices = newSuperSelection;

                foreach(var c in filteredChoices)
                    subSelection.AddChoice(c);

                //Present The Void if no selections are possible
                if(subSelection.Count == 0) {
                    currentSelection.Add((PickupIndex.none, new Color(0.1f, 0.1f, 0.1f)));
                    continue;
                }
                //Otherwise, select an item
                var next = subSelection.EvaluateToChoiceIndex(KnowledgeArtifact.instance.rng.nextNormalizedFloat);
                var sel = subSelection.GetChoice(next);
                var pdef = PickupCatalog.GetPickupDef(sel.value);
                var idef = ItemCatalog.GetItemDef(pdef.itemIndex);

                //Remove the item's tags from a guaranteed tag count and use that to determine border color (TODO: randomize order?)
                Color selBorderColor = new Color(1f, 1f, 1f);
                if(remainingGuaranteedTags.Count() > 0) {
                    foreach(var key in guaranteedOfAnyTag.Keys.ToArray()) {
                        if(key.Intersect(idef.tags).Any()) {
                            selBorderColor = guaranteedOfAnyTag[key].borderColor;
                            guaranteedOfAnyTag[key] = (guaranteedOfAnyTag[key].borderColor, guaranteedOfAnyTag[key].remaining - 1);
                            break;
                        }
                    }
                }
                currentSelection.Add((sel.value, selBorderColor));

                //Remove the new item from the pool to prevent duplicates
                newSuperSelection.Remove(sel);


                //If guaranteed tags have no selections left, remove them
                var remainingTagsInPool = newSuperSelection.SelectMany(c => {
                    var lpdef = PickupCatalog.GetPickupDef(c.value);
                    var lidef = ItemCatalog.GetItemDef(lpdef.itemIndex);
                    if(!lidef) return new ItemTag[0];
                    return lidef.tags;
                });
                foreach(var key in guaranteedOfAnyTag.Keys.ToArray()) {
                    if(!key.Intersect(remainingTagsInPool).Any())
                        guaranteedOfAnyTag[key] = (guaranteedOfAnyTag[key].borderColor, 0);
                }

                //Remove limited item tiers from the pool
                if(idef) {
                    foreach(var key in maxOfAnyTier.Keys.ToArray()) {
                        if(key.Contains(idef.tier)) {
                            maxOfAnyTier[key]--;
                            if(maxOfAnyTier[key] == 0) {
                                newSuperSelection.RemoveAll(c => {
                                    var lpdef = PickupCatalog.GetPickupDef(c.value);
                                    var lidef = ItemCatalog.GetItemDef(lpdef.itemIndex);
                                    if(!lidef) return false;
                                    return key.Contains(lidef.tier);
                                });
                            }
                        }
                    }
                }
            }
            for(int i = 0; i < ArtifactOfKnowledgePlugin.itemSelectionConfig.GearSelectionSize; i++) {
                if(newGearSuperSelection.Count == 0) {
                    currentSelection.Add((PickupIndex.none, new Color(0.5f, 0.5f, 0.5f)));
                } else {
                    var next = newGearSuperSelection.EvaluateToChoiceIndex(KnowledgeArtifact.instance.rng.nextNormalizedFloat);
                    currentSelection.Add((newGearSuperSelection.GetChoice(next).value, new Color(1f, 0.6f, 0.2f)));
                    newGearSuperSelection.RemoveChoice(next);
                }
            }

            RpcSyncLists(this.banished.Select(i => i.value).ToArray(), this.currentSelection.Select(kvp => kvp.index.value).ToArray(), this.currentSelection.Select(kvp => kvp.borderColor).ToArray());
        }
        #endregion

        #region Authority/Client
        [ClientRpc]
        public void RpcSyncLists(int[] banish, int[] selIndices, Color[] selColors) {
            this.banished.Clear();
            this.banished.UnionWith(banish.Select(i => new PickupIndex(i)));

            this.currentSelection.Clear();
            for(var i = 0; i < selIndices.Length; i++)
                this.currentSelection.Add((new PickupIndex(selIndices[i]), selColors[i]));
            ClientUpdateUpgradePanel();
            ClientUpdateXpBar();
        }

        [ClientRpc]
        public void RpcForceUpdateUI(bool upgradePanel) {
            ClientUpdateXpBar();
            if(upgradePanel)
                ClientUpdateUpgradePanel();
        }

        [Client]
        public void ClientUpdateUpgradePanel() {
            if(currentUpgradePanel) {
                var kpp = currentUpgradePanel.GetComponent<KnowledgePickerPanel>();
                kpp.SetPickupOptions(currentSelection.ToArray());
                var ltmc = kpp.transform.Find("MainPanel/Juice/Label").GetComponent<LanguageTextMeshController>();
                ltmc.formatArgs = new object[] { unspentUpgrades, rerolls };
            }
        }

        [ClientRpc]
        void RpcLevelUpEvent() {
            if(Util.HasEffectiveAuthority(gameObject) && currentHud && targetMasterObject.TryGetComponent<CharacterMaster>(out var master) && master.hasBody) {
                Util.PlaySound("Play_UI_item_land_tier3", master.GetBody().gameObject);
            }
        }

        [Client]
        public void ClientDiscoverHud() {
            if(!currentHud) currentHud = HUD.readOnlyInstanceList.FirstOrDefault(x => x.targetMaster && x.targetMaster.gameObject == targetMasterObject);
            if(!currentHud) return;
        }

        [Client]
        public void ClientUpdateXpBar() {
            if(!Util.HasEffectiveAuthority(gameObject)) return;
            if(!currentHud) return;
            if(ArtifactOfKnowledgePlugin.clientConfig.XpBarLocation != ArtifactOfKnowledgePlugin.ClientConfig.UICluster.Nowhere) {
                if(!currentXpBar) {
                    currentXpBar = currentHud.transform.GetComponentInChildren<KnowledgeXpBar>();
                }
                if(currentXpBar) currentXpBar.SetFill(Mathf.InverseLerp((float)thisLevelXp, (float)nextLevelXp, (float)xp), unspentUpgrades, spentUpgrades);
            }
        }

        [ClientRpc]
        public void RpcDisplayError(UpgradeActionCode errorCode) {
            if(!Util.HasEffectiveAuthority(targetMasterObject) || !currentUpgradePanel) return;
            Util.PlaySound("Play_UI_insufficient_funds", RoR2Application.instance.gameObject);
        }

        [Client]
        public void ClientShowUpgradePanel() {
            if(!Util.HasEffectiveAuthority(gameObject) || currentUpgradePanel || !currentHud) return;
            currentUpgradePanel = GameObject.Instantiate(KnowledgePickerPanelModule.instance.panelPrefab, currentHud.transform.Find("MainContainer").Find("MainUIArea"));
            var rerollButton = currentUpgradePanel.transform.Find("MainPanel/Juice/RerollButton").gameObject.GetComponent<HGButton>();
            rerollButton.onClick.AddListener(() => { this.CmdReroll(); });
            var kpp = currentUpgradePanel.GetComponent<KnowledgePickerPanel>();
            kpp.onButtonPressed = (i) => { this.CmdSelect(i); };
            ClientUpdateUpgradePanel();
        }

        [ClientRpc]
        public void RpcRemoteCloseUpgradePanel() {
            if(!Util.HasEffectiveAuthority(gameObject) || !currentUpgradePanel) return;
            GameObject.Destroy(currentUpgradePanel);
        }
        #endregion
    }
}
