using UnityEditor;
using UnityEngine;
using U3D.Net;

namespace U3D.Editor
{
    [CustomPropertyDrawer(typeof(NetPrefab))]
    public class NetPrefabDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            SerializedProperty prefabProp = property.FindPropertyRelative("prefab");

            EditorGUI.BeginProperty(position, label, property);

            Rect fieldRect = EditorGUI.PrefixLabel(
                position, GUIUtility.GetControlID(FocusType.Passive), label);

            prefabProp.objectReferenceValue = EditorGUI.ObjectField(
                fieldRect, prefabProp.objectReferenceValue, typeof(GameObject), false);

            EditorGUI.EndProperty();
        }
    }
}