using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VF.Builder;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using Object = UnityEngine.Object;

namespace VF.Menu {
    public class AvatarCleaner {
        public static List<string> Cleanup(
            GameObject avatarObj,
            bool perform = false,
            Func<GameObject, bool> ShouldRemoveObj = null,
            Func<Component, bool> ShouldRemoveComponent = null,
            Func<Object, bool> ShouldRemoveAsset = null,
            Func<string, bool> ShouldRemoveLayer = null,
            Func<string, bool> ShouldRemoveParam = null
        ) {
            var removeItems = new List<string>();
            
            string GetPath(GameObject obj) {
                return AnimationUtility.CalculateTransformPath(obj.transform, avatarObj.transform);
            }

            if (ShouldRemoveObj != null || ShouldRemoveComponent != null) {
                var checkStack = new Stack<Transform>();
                checkStack.Push(avatarObj.transform);
                while (checkStack.Count > 0) {
                    var t = checkStack.Pop();
                    var obj = t.gameObject;

                    if (ShouldRemoveObj != null && ShouldRemoveObj(obj)) {
                        removeItems.Add("Object: " + GetPath(obj));
                        if (perform) RemoveObject(obj);
                    } else {
                        if (ShouldRemoveComponent != null) {
                            foreach (var component in obj.GetComponents<Component>()) {
                                if (!(component is Transform) && ShouldRemoveComponent(component)) {
                                    removeItems.Add("Component: " + component.GetType().Name + " on " + GetPath(obj));
                                    if (perform) RemoveComponent(component);
                                }
                            }
                        }

                        // Make sure RemoveComponent didn't remove this object!
                        if (t) {
                            foreach (Transform t2 in t) checkStack.Push(t2);
                        }
                    }
                }
            }

            var avatar = avatarObj.GetComponent<VRCAvatarDescriptor>();
            if (avatar != null) {
                foreach (var (controller, set, type) in VRCAvatarUtils.GetAllControllers(avatar)) {
                    if (controller == null) continue;
                    var typeName = VRCFEnumUtils.GetName(type);
                    if (ShouldRemoveAsset != null && ShouldRemoveAsset(controller)) {
                        removeItems.Add("Avatar Controller: " + typeName);
                        if (perform) set(null);
                    } else {
                        var vfac = new VFAController(controller, type);
                        var removedLayers = new HashSet<AnimatorStateMachine>();
                        if (ShouldRemoveLayer != null) {
                            for (var i = 0; i < controller.layers.Length; i++) {
                                var layer = controller.layers[i];
                                if (!ShouldRemoveLayer(layer.name)) continue;
                                removeItems.Add(typeName + " Layer: " + layer.name);
                                removedLayers.Add(layer.stateMachine);
                                if (perform) {
                                    vfac.RemoveLayer(i);
                                    i--;
                                }
                            }
                        }

                        if (ShouldRemoveParam != null) {
                            for (var i = 0; i < controller.parameters.Length; i++) {
                                var prm = controller.parameters[i].name;
                                if (!ShouldRemoveParam(prm)) continue;

                                var prmUsed = controller.layers
                                    .Where(layer => !removedLayers.Contains(layer.stateMachine))
                                    .Any(layer => IsParamUsed(layer, prm));
                                if (prmUsed) continue;

                                removeItems.Add(typeName + " Parameter: " + prm);
                                if (perform) {
                                    controller.RemoveParameter(i);
                                    i--;
                                }
                            }
                        }

                        if (perform) EditorUtility.SetDirty(controller);
                    }
                }

                var syncParams = VRCAvatarUtils.GetAvatarParams(avatar);
                if (syncParams != null) {
                    if (ShouldRemoveAsset != null && ShouldRemoveAsset(syncParams)) {
                        removeItems.Add("All Synced Params");
                        if (perform) VRCAvatarUtils.SetAvatarParams(avatar, null);
                    } else {
                        var prms = new List<VRCExpressionParameters.Parameter>(syncParams.parameters);
                        for (var i = 0; i < prms.Count; i++) {
                            if (ShouldRemoveParam != null && ShouldRemoveParam(prms[i].name)) {
                                removeItems.Add("Synced Param: " + prms[i].name);
                                if (perform) {
                                    prms.RemoveAt(i);
                                    i--;
                                }
                            }
                        }

                        if (perform) {
                            syncParams.parameters = prms.ToArray();
                            EditorUtility.SetDirty(syncParams);
                        }
                    }
                }

                var m = VRCAvatarUtils.GetAvatarMenu(avatar);
                if (m != null) {
                    if (ShouldRemoveAsset != null && ShouldRemoveAsset(m)) {
                        removeItems.Add("All Avatar Menus");
                        if (perform) VRCAvatarUtils.SetAvatarMenu(avatar, null);
                    } else {
                        MenuSplitter.ForEachMenu(m, ForEachItem: (item, path) => {
                            var shouldRemove =
                                item.type == VRCExpressionsMenu.Control.ControlType.SubMenu
                                && item.subMenu
                                && ShouldRemoveAsset != null
                                && ShouldRemoveAsset(item.subMenu);
                            shouldRemove |=
                                item.type == VRCExpressionsMenu.Control.ControlType.Toggle
                                && item.parameter != null
                                && ShouldRemoveParam != null
                                && ShouldRemoveParam(item.parameter.name);
                            if (shouldRemove) {
                                removeItems.Add("Menu Item: " + string.Join("/", path));
                                return perform
                                    ? MenuSplitter.ForEachMenuItemResult.Delete
                                    : MenuSplitter.ForEachMenuItemResult.Skip;
                            }
                            return MenuSplitter.ForEachMenuItemResult.Continue;
                        });
                    }
                }
            }

            return removeItems;
        }
        
        public static void RemoveComponent(Component c) {
            if (c.gameObject.GetComponents<Component>().Length == 2 && c.gameObject.transform.childCount == 0)
                RemoveObject(c.gameObject);
            else
                Object.DestroyImmediate(c);
        }
        public static void RemoveObject(GameObject obj) {
            if (!PrefabUtility.IsPartOfPrefabInstance(obj) || PrefabUtility.IsOutermostPrefabInstanceRoot(obj)) {
                Object.DestroyImmediate(obj);
            } else {
                foreach (var component in obj.GetComponentsInChildren<Component>(true)) {
                    if (!(component is Transform)) {
                        Object.DestroyImmediate(component);
                    }
                }
                obj.name = "_deleted_" + obj.name;
            }
        }

        private static bool IsParamUsed(AnimatorControllerLayer layer, string param) {
            var isUsed = false;
            AnimatorIterator.ForEachTransition(layer.stateMachine, t => {
                foreach (var c in t.conditions) {
                    isUsed |= c.parameter == param;
                }
            });
            AnimatorIterator.ForEachState(layer.stateMachine, state => {
                isUsed |= state.speedParameter == param;
                isUsed |= state.cycleOffsetParameter == param;
                isUsed |= state.mirrorParameter == param;
                isUsed |= state.timeParameter == param;
            });
            AnimatorIterator.ForEachBlendTree(layer.stateMachine, tree => {
                isUsed |= tree.blendParameter == param;
                isUsed |= tree.blendParameterY == param;
            });
            return isUsed;
        }
    }
}