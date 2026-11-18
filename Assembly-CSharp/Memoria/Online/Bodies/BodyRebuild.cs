using Memoria.Prime;
using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Memoria.Online.Bodies
{
    /// <summary>
    /// Replaces the body of a field object that already exists (Player ↔ Cast mode), the one place a body changes
    /// on a live object. The object (script state, UID, position, floor, size, current animation) stays; only its
    /// model is rebuilt the way SetModel builds it, and its animations are translated by action name.
    /// </summary>
    internal static class BodyRebuild
    {
        public static Boolean Rebuild(Actor actor, CharacterBody from, CharacterBody to)
        {
            EventEngine engine = PersistenSingleton<EventEngine>.Instance;
            FieldMap fieldMap = engine?.fieldmap;
            GameObject oldGo = actor?.go;
            if (fieldMap == null || oldGo == null || engine.gMode != 1)
                return false;
            if (!FF9BattleDB.GEO.TryGetValue(to.Model, out String modelName))
            {
                Log.Warning($"[Online] Bodies: no geometry for model {to.Model} ({to.Name})");
                return false;
            }

            FieldMapActorController oldController = actor.fieldMapActorController;
            FieldMapActor oldFieldActor = oldGo.GetComponent<FieldMapActor>();
            Boolean isPlayer = oldController != null && oldController.isPlayer;
            Vector3 position = oldController != null ? oldController.curPos : oldGo.transform.localPosition;
            Quaternion rotation = oldGo.transform.localRotation;
            Vector3 scale = oldGo.transform.localScale;
            Single radius = oldController != null ? oldController.radius : actor.bgiRad * 4f;
            Boolean active = oldGo.activeSelf;
            String oldName = oldGo.name; // "Player" for the player (looked up by name), else "obj<uid>"
            Transform oldParent = oldGo.transform.parent; // not the field when attached to something (the tram)
            Vector3 localPosition = oldGo.transform.localPosition;
            // Objects attached to this body's bones (AttachObject): moved to the same bones of the new body
            List<KeyValuePair<Transform, String>> carried = new List<KeyValuePair<Transform, String>>();
            foreach (FieldMapActor other in oldGo.GetComponentsInChildren<FieldMapActor>(true))
                if (other.gameObject != oldGo)
                    carried.Add(new KeyValuePair<Transform, String>(other.transform, other.transform.parent.name));

            GameObject go = ModelFactory.CreateModel(modelName, false, true, Configuration.Graphics.ElementsSmoothTexture);
            if (go == null)
            {
                Log.Warning($"[Online] Bodies: model {modelName} could not be created");
                return false;
            }
            GeoTexAnim.addTexAnim(go, modelName);
            actor.garnet = ModelFactory.garnetShortHairTable.Contains(modelName);
            if (actor.garnet)
                actor.shortHair = FF9StateSystem.EventState.ScenarioCounter >= 10300;
            Int32 meshCount = 0;
            foreach (Transform child in go.transform)
                if (child.name.Contains("mesh"))
                    meshCount++;
            if (actor.garnet)
                meshCount++;
            actor.meshIsRendering = new Boolean[meshCount];
            for (Int32 i = 0; i < meshCount; i++)
                actor.meshIsRendering[i] = true;

            // Retire the old body (as EventEngine.DisposeObj does). RULE 6: its shadow is only hidden now; destroying
            // it before the behaviour system has disposed of the body makes FieldMapActor.HonoLateUpdate throw, which
            // stops that system's whole update (nothing moves) and the disposal never happens.
            oldGo.name += "_replaced";
            BodySwap.TrackReplaced(oldGo, oldFieldActor);
            if (oldController != null)
                oldController.UnregisterHonoBehavior(true);
            if (oldFieldActor != null)
            {
                if (oldFieldActor.shadowTran != null)
                    oldFieldActor.shadowTran.gameObject.SetActive(false);
                oldFieldActor.UnregisterHonoBehavior(true);
            }
            else
            {
                Object.Destroy(oldGo);
            }
            oldGo.SetActive(false); // never drawn again, even before the system disposes of it

            // Register the new one under the same UID (as SetModel does), keeping the field's per-character state
            actor.model = to.Model;
            actor.eye = (Int16)(-4 * to.HeadHeight);
            actor.go = go;
            go.name = oldName;
            if (!FF9StateSystem.Common.FF9.charArray.TryGetValue(actor.uid, out FF9Char ff9Char))
                FF9StateSystem.Common.FF9.charArray[actor.uid] = ff9Char = new FF9Char { evt = actor };
            ff9Char.geo = go;
            ff9Char.attr &= ~0x100u; // the field lighting is applied again to the new body (fldmcf)
            if (!FF9StateSystem.Field.FF9Field.loc.map.charStateArray.ContainsKey(actor.uid))
                FF9StateSystem.Field.FF9Field.loc.map.charStateArray[actor.uid] = new FF9FieldCharState();
            if (!FF9StateSystem.Field.FF9Field.loc.map.shadowArray.ContainsKey(actor.uid))
                FF9StateSystem.Field.FF9Field.loc.map.shadowArray[actor.uid] = new FF9Shadow();
            fieldMap.AddFieldChar(go, position, rotation, isPlayer, actor, false);
            if (actor.fieldMapActorController != null)
                actor.fieldMapActorController.radius = radius;
            if (oldParent != null && oldParent != fieldMap.transform)
            {
                go.transform.parent = oldParent;
                go.transform.localPosition = localPosition;
                go.transform.localRotation = rotation;
            }
            go.transform.localScale = scale; // fields that resize characters (SetObjectSize)
            foreach (KeyValuePair<Transform, String> kv in carried)
            {
                if (kv.Key == null)
                    continue;
                Transform bone = go.transform.GetChildByName(kv.Value) ?? go.transform;
                Vector3 p = kv.Key.localPosition;
                Quaternion r = kv.Key.localRotation;
                Vector3 sc = kv.Key.localScale;
                kv.Key.parent = bone;
                kv.Key.localPosition = p;
                kv.Key.localRotation = r;
                kv.Key.localScale = sc;
            }
            // Not destroyed here: the behaviour system disposes of it on its next update (destroying it first
            // breaks that update); BodySwap destroys it a second later if it's still there
            if (!active)
                go.SetActive(false);

            // Animations: everything the actor holds, translated to the new body
            foreach (UInt16 anim in to.Actions.Values)
                AnimationFactory.AddAnimWithAnimatioName(go, FF9DBAll.AnimationDB.GetValue(anim));
            actor.idle = Translate(from, to, actor.idle);
            actor.walk = Translate(from, to, actor.walk);
            actor.run = Translate(from, to, actor.run);
            actor.turnl = Translate(from, to, actor.turnl);
            actor.turnr = Translate(from, to, actor.turnr);
            actor.sleep = Translate(from, to, actor.sleep);
            actor.jump = Translate(from, to, actor.jump);
            actor.lastAnim = Translate(from, to, actor.lastAnim);
            actor.anim = Translate(from, to, actor.anim);
            AnimationFactory.AddAnimWithAnimatioName(go, FF9DBAll.AnimationDB.GetValue(actor.anim));
            actor.frameN = (Byte)EventEngineUtils.GetCharAnimFrame(go, actor.anim);
            if (actor.frameN > 0 && actor.animFrame >= actor.frameN)
                actor.animFrame = (Byte)(actor.frameN - 1);
            if (actor.isShadowOff)
                ff9shadow.FF9ShadowOffField(actor.uid);
            return true;
        }

        /// <summary>An animation of <paramref name="from"/> → the same action of <paramref name="to"/> (unknown → Stand).</summary>
        public static UInt16 Translate(CharacterBody from, CharacterBody to, UInt16 anim)
        {
            if (from.ActionOf.TryGetValue(anim, out String action))
                return to.Get(action);
            if (to.ActionOf.ContainsKey(anim))
                return anim;
            return to.Get(CharacterBodies.Stand);
        }
    }
}
