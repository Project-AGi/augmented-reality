using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.EnhancedTouch;
using UnityEngine.InputSystem.XR;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using Unity.XR.CoreUtils;
using Touch = UnityEngine.InputSystem.EnhancedTouch.Touch;
using TouchPhase = UnityEngine.InputSystem.TouchPhase;
using VRM;

namespace Aiva.AivaAR
{
    public sealed class AivaArApp : MonoBehaviour
    {
        private static readonly List<ARRaycastHit> Hits = new();
        private static readonly BlendShapeKey Mouth = BlendShapeKey.CreateFromPreset(BlendShapePreset.A);

        private InputAction _positionAction;
        private InputAction _rotationAction;
        private ARRaycastManager _raycastManager;
        private ARPlaneManager _planeManager;
        private ARAnchorManager _anchorManager;
        private ARAnchor _anchor;
        private bool _placing;
        private AivaChamber _chamber;
        private AivaVrmLoader _loader;
        private AivaReplyStream _replies;
        private VRMBlendShapeProxy _face;
        private TouchScreenKeyboard _addressKeyboard;
        private string _telepathyAddress = "";
        private float _talkUntil;
        private float _mouthWeight;
        private string _status = "Move the phone slowly so ARCore can find the floor.";

        private float UiScale => Mathf.Max(1f, Screen.dpi / 180f);
        private float ActionButtonHeight => 56f * UiScale;
        private float ConnectionPanelHeight => 172f * UiScale;

        private void Awake()
        {
            EnhancedTouchSupport.Enable();
            CreateArRig();
            _chamber = AivaChamber.Create();
            _chamber.gameObject.SetActive(false);
            _replies = gameObject.AddComponent<AivaReplyStream>();
            _replies.TextArrived += AnimateReply;
            _replies.ReplyInterrupted += StopReplyAnimation;

            _loader = gameObject.AddComponent<AivaVrmLoader>();
            _loader.Loaded += model =>
            {
                _chamber.Actions.Bind(model);
                _face = model.GetComponent<VRMBlendShapeProxy>();
                _status = "AIVA is ready. Tap a floor point to place her chamber.";
            };
            _loader.Failed += error => _status = error;
            _loader.LoadInto(_chamber.ModelMount);
        }

        private void CreateArRig()
        {
            var sessionObject = new GameObject("AR Session");
            sessionObject.AddComponent<ARSession>();
            sessionObject.AddComponent<ARInputManager>();

            var originObject = new GameObject("XR Origin");
            originObject.SetActive(false);
            var origin = originObject.AddComponent<XROrigin>();
            _raycastManager = originObject.AddComponent<ARRaycastManager>();
            _planeManager = originObject.AddComponent<ARPlaneManager>();
            _anchorManager = originObject.AddComponent<ARAnchorManager>();

            var offset = new GameObject("Camera Offset");
            offset.transform.SetParent(originObject.transform, false);
            var cameraObject = new GameObject("AR Camera");
            cameraObject.transform.SetParent(offset.transform, false);
            cameraObject.tag = "MainCamera";
            var camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            cameraObject.AddComponent<AudioListener>();
            var poseDriver = cameraObject.AddComponent<TrackedPoseDriver>();
            _positionAction = new InputAction("AR camera position", InputActionType.Value, "<XRHMD>/centerEyePosition");
            _rotationAction = new InputAction("AR camera rotation", InputActionType.Value, "<XRHMD>/centerEyeRotation");
            _positionAction.Enable();
            _rotationAction.Enable();
            poseDriver.positionInput = new InputActionProperty(_positionAction);
            poseDriver.rotationInput = new InputActionProperty(_rotationAction);
            cameraObject.AddComponent<ARCameraManager>();
            cameraObject.AddComponent<ARCameraBackground>();
            origin.CameraFloorOffsetObject = offset;
            origin.Camera = camera;
            originObject.SetActive(true);
        }

        private void Update()
        {
            if (_addressKeyboard != null && _addressKeyboard.status != TouchScreenKeyboard.Status.Visible)
            {
                if (_addressKeyboard.status == TouchScreenKeyboard.Status.Done)
                    _telepathyAddress = _addressKeyboard.text;
                _addressKeyboard = null;
            }

            UpdateMouth();
            if (_addressKeyboard != null) return;
            if (Touch.activeTouches.Count == 0 || Touch.activeTouches[0].phase != TouchPhase.Began) return;
            var touch = Touch.activeTouches[0];
            if (touch.screenPosition.y < ActionButtonHeight + 20f
                || touch.screenPosition.y > Screen.height - ConnectionPanelHeight) return;
            PlaceAt(touch.screenPosition);
        }

        private void AnimateReply() => _talkUntil = Time.unscaledTime + 0.28f;

        private void StopReplyAnimation() => _talkUntil = 0f;

        private void UpdateMouth()
        {
            if (_face == null || !_face.isActiveAndEnabled) return;
            var time = Time.unscaledTime;
            // Reply events carry text, so this is a visual cue for reply activity.
            var target = time < _talkUntil ? 0.35f + 0.28f * Mathf.Sin(time * 32f) : 0f;
            var weight = Mathf.MoveTowards(_mouthWeight, target, 7f * Time.unscaledDeltaTime);
            if (weight == _mouthWeight) return;
            _mouthWeight = weight;
            _face.ImmediatelySetValue(Mouth, weight);
        }

        private void OnDestroy()
        {
            if (_replies != null)
            {
                _replies.TextArrived -= AnimateReply;
                _replies.ReplyInterrupted -= StopReplyAnimation;
            }
            _positionAction?.Dispose();
            _rotationAction?.Dispose();
            if (EnhancedTouchSupport.enabled) EnhancedTouchSupport.Disable();
        }

        private async void PlaceAt(Vector2 screenPoint)
        {
            if (_placing || _anchorManager.subsystem?.running != true) return;
            if (!_raycastManager.Raycast(screenPoint, Hits, TrackableType.PlaneWithinPolygon))
            {
                _status = "No floor found there. Move the phone slowly and try again.";
                return;
            }

            var pose = Hits[0].pose;
            var camera = Camera.main;
            var towardCamera = camera == null ? Vector3.forward : camera.transform.position - pose.position;
            towardCamera.y = 0f;
            if (towardCamera.sqrMagnitude > 0.001f)
            {
                pose.rotation = Quaternion.LookRotation(towardCamera.normalized, Vector3.up);
            }

            _placing = true;
            _status = "Anchoring AIVA’s chamber…";
            try
            {
                var result = await _anchorManager.TryAddAnchorAsync(pose);
                if (this == null) return;
                if (!result.status.IsSuccess() || result.value == null)
                {
                    _status = "Could not anchor the chamber. Tap a detected floor point again.";
                    return;
                }

                var previous = _anchor;
                _anchor = result.value;
                _chamber.transform.SetParent(_anchor.transform, false);
                _chamber.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
                _chamber.gameObject.SetActive(true);
                if (previous != null && !_anchorManager.TryRemoveAnchor(previous))
                    Destroy(previous.gameObject);
                _status = "AIVA’s chamber is anchored here for this AR session.";
                _planeManager.enabled = false;
            }
            catch (System.Exception error)
            {
                if (this != null)
                {
                    _status = "Could not anchor the chamber. Tap a detected floor point again.";
                    Debug.LogWarning($"AIVA AR anchor placement failed: {error.Message}");
                }
            }
            finally
            {
                _placing = false;
            }
        }

        private void OnGUI()
        {
            var scale = UiScale;
            var style = new GUIStyle(GUI.skin.box)
            {
                fontSize = Mathf.RoundToInt(16f * scale),
                alignment = TextAnchor.MiddleLeft,
                wordWrap = true,
                padding = new RectOffset(18, 18, 12, 12)
            };
            GUI.Box(new Rect(16, 16, Screen.width - 32, 76 * scale), _status, style);

            DrawReplyControls(style);

            var buttonWidth = (Screen.width - 48) / 3f;
            var buttonHeight = ActionButtonHeight;
            var y = Screen.height - buttonHeight - 20f;
            if (GUI.Button(new Rect(12, y, buttonWidth, buttonHeight), "Place / Move"))
            {
                _planeManager.enabled = true;
                _status = "Tap the floor where the chamber should stand.";
            }
            if (GUI.Button(new Rect(24 + buttonWidth, y, buttonWidth, buttonHeight), "Wave"))
            {
                _chamber.Actions.Wave();
            }
            if (GUI.Button(new Rect(36 + buttonWidth * 2, y, buttonWidth, buttonHeight), "Turn"))
            {
                _chamber.Actions.TurnAround();
            }
        }

        private void DrawReplyControls(GUIStyle statusStyle)
        {
            var width = Screen.width - 32f;
            var y = 20f + 76f * UiScale;
            var height = 32f * UiScale;
            var connectWidth = width * 0.32f;
            var addressRect = new Rect(16, y, width - connectWidth - 8f, height);
            var addressLabel = string.IsNullOrEmpty(_telepathyAddress) ? "Telepathy HTTPS address" : _telepathyAddress;
            var previous = GUI.enabled;
            GUI.enabled = !_replies.Active && _addressKeyboard == null;
            if (TouchScreenKeyboard.isSupported)
            {
                if (GUI.Button(addressRect, addressLabel))
                    _addressKeyboard = TouchScreenKeyboard.Open(_telepathyAddress, TouchScreenKeyboardType.URL);
            }
            else
            {
                _telepathyAddress = GUI.TextField(addressRect, _telepathyAddress, 2048);
            }

            GUI.enabled = previous;
            if (GUI.Button(new Rect(16f + width - connectWidth, y, connectWidth, height),
                _replies.Active ? "Disconnect" : "Connect"))
            {
                if (_replies.Active) _replies.Disconnect();
                else _replies.Connect(_telepathyAddress);
            }
            GUI.Box(new Rect(16, y + height + 4f, width, 32f * UiScale), _replies.Status, statusStyle);
        }
    }
}

