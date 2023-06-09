﻿using RoR2;
using System;
using TILER2;
using UnityEngine;
using UnityEngine.Networking;

namespace ThinkInvisible.ArtifactOfKnowledge {
    public class KnowledgeArtifact : Artifact<KnowledgeArtifact> {

        public override bool managedEnable => false;

        ////// Config //////



        ////// Other Fields/Properties //////



        ////// TILER2 Module Setup //////

        public KnowledgeArtifact() {
            iconResource = ArtifactOfKnowledgePlugin.resources.LoadAsset<Sprite>("Assets/ArtifactOfKnowledge/Textures/knowledge_on.png");
            iconResourceDisabled = ArtifactOfKnowledgePlugin.resources.LoadAsset<Sprite>("Assets/ArtifactOfKnowledge/Textures/knowledge_off.png");
        }

        public override void SetupAttributes() {
            base.SetupAttributes();
        }

        public override void Install() {
            base.Install();

            On.RoR2.Run.OnServerCharacterBodySpawned += Run_OnServerCharacterBodySpawned;
            RoR2.Run.onRunDestroyGlobal += Run_onRunDestroyGlobal;
            RoR2.TeleporterInteraction.onTeleporterChargedGlobal += TeleporterInteraction_onTeleporterChargedGlobal;
            SceneDirector.onPrePopulateSceneServer += OnPrePopulateSceneServer;
            SceneDirector.onGenerateInteractableCardSelection += OnGenerateInteractableCardSelection;
            DirectorCardCategorySelection.calcCardWeight += CalcCardWeight;
            On.RoR2.UI.HUD.Awake += HUD_Awake;
        }

        public override void Uninstall() {
            base.Uninstall();

            On.RoR2.Run.OnServerCharacterBodySpawned -= Run_OnServerCharacterBodySpawned;
            RoR2.Run.onRunDestroyGlobal -= Run_onRunDestroyGlobal;
            RoR2.TeleporterInteraction.onTeleporterChargedGlobal -= TeleporterInteraction_onTeleporterChargedGlobal;
            SceneDirector.onPrePopulateSceneServer -= OnPrePopulateSceneServer;
            SceneDirector.onGenerateInteractableCardSelection -= OnGenerateInteractableCardSelection;
            DirectorCardCategorySelection.calcCardWeight -= CalcCardWeight;
            On.RoR2.UI.HUD.Awake -= HUD_Awake;
        }



        ////// Hooks //////

        private void HUD_Awake(On.RoR2.UI.HUD.orig_Awake orig, RoR2.UI.HUD self) {
            orig(self);
            if(IsActiveAndEnabled())
                KnowledgeXpBar.ModifyHud(self);
        }

        private void Run_OnServerCharacterBodySpawned(On.RoR2.Run.orig_OnServerCharacterBodySpawned orig, Run self, CharacterBody characterBody) {
            orig(self, characterBody);
            if(!IsActiveAndEnabled() || !NetworkServer.active || !characterBody || !characterBody.master || characterBody.teamComponent.teamIndex != TeamIndex.Player || !characterBody.isPlayerControlled) return;
            var master = characterBody.master;
            if(KnowledgeCharacterManager.readOnlyInstancesByTarget.ContainsKey(master.gameObject)) return;
            var kcm = GameObject.Instantiate(KnowledgeCharacterManagerModule.instance.managerPrefab);
            var cao = master.GetComponent<NetworkIdentity>().clientAuthorityOwner;
            if(cao == null) {
                NetworkServer.Spawn(kcm);
            } else {
                NetworkServer.SpawnWithClientAuthority(kcm, cao);
            }
            var kcmCpt = kcm.GetComponent<KnowledgeCharacterManager>();
            kcmCpt.ServerGrantRerolls(ArtifactOfKnowledgePlugin.ServerConfig.StartingRerolls);
            kcmCpt.ServerAssignAndStart(master.gameObject);
        }

        private void Run_onRunDestroyGlobal(Run obj) {
            foreach(var kcm in GameObject.FindObjectsOfType<KnowledgeCharacterManager>()) {
                GameObject.Destroy(kcm.gameObject);
            }
        }

        private void TeleporterInteraction_onTeleporterChargedGlobal(TeleporterInteraction obj) {
            if(!NetworkServer.active) return;
            foreach(var kcm in GameObject.FindObjectsOfType<KnowledgeCharacterManager>()) {
                kcm.ServerGrantRerolls(ArtifactOfKnowledgePlugin.ServerConfig.RerollsPerStage);
            }
        }

        private void CalcCardWeight(DirectorCard card, ref float weight) {
            if(!IsActiveAndEnabled() || RunArtifactManager.instance.IsArtifactEnabled(RoR2Content.Artifacts.Sacrifice)) return; //sacrifice performs same code
            var isc = card.spawnCard as InteractableSpawnCard;
            if(isc != null) weight *= isc.weightScalarWhenSacrificeArtifactEnabled;
        }

        private void OnGenerateInteractableCardSelection(SceneDirector sceneDirector, DirectorCardCategorySelection dccs) {
            if(!IsActiveAndEnabled() || RunArtifactManager.instance.IsArtifactEnabled(RoR2Content.Artifacts.Sacrifice)) return; //sacrifice performs same code
            dccs.RemoveCardsThatFailFilter((card) => {
                var isc = card.spawnCard as InteractableSpawnCard;
                return isc == null || !isc.skipSpawnWhenSacrificeArtifactEnabled;
            });
        }

        private void OnPrePopulateSceneServer(SceneDirector sceneDirector) {
            if(!IsActiveAndEnabled() || RunArtifactManager.instance.IsArtifactEnabled(RoR2Content.Artifacts.Sacrifice)) return; //sacrifice performs same code
            sceneDirector.onPopulateCreditMultiplier *= 0.5f;
        }
    }
}