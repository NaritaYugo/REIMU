using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(CharacterController))]
public class PlayerController : MonoBehaviour
{
    [Header("Movement")]
    public float moveSpeed = 5f;
    public float jumpForce = 20f;

    [Header("Camera Tracking & Look")]
    public Transform cameraTransform;
    public Vector3 cameraOffset = new Vector3(0, 2f, -5f);
    public float cameraFollowSpeed = 10f;
    
    public float lookSpeedX = 5f; 
    public float lookSpeedY = 3f;

    private CharacterController controller;
    private float verticalVelocity = 0f;
    private float cameraPitch = 0f;

    // --- Input System 用のAction定義 ---
    private InputAction moveAction;
    private InputAction lookAction;
    private InputAction jumpAction;
    private InputAction rightClickAction;

    void Awake()
    {
        moveAction = new InputAction("Move", binding: "<Gamepad>/leftStick");
        moveAction.AddCompositeBinding("Dpad")
            .With("Up", "<Keyboard>/w")
            .With("Down", "<Keyboard>/s")
            .With("Left", "<Keyboard>/a")
            .With("Right", "<Keyboard>/d");

        lookAction = new InputAction("Look", binding: "<Pointer>/delta");
        lookAction.AddBinding("<Gamepad>/rightStick");

        jumpAction = new InputAction("Jump", binding: "<Keyboard>/space");
        jumpAction.AddBinding("<Gamepad>/buttonSouth");

        rightClickAction = new InputAction("RightClick", binding: "<Mouse>/rightButton");
    }

    void OnEnable()
    {
        moveAction.Enable();
        lookAction.Enable();
        jumpAction.Enable();
        rightClickAction.Enable();
    }

    void OnDisable()
    {
        moveAction.Disable();
        lookAction.Disable();
        jumpAction.Disable();
        rightClickAction.Disable();
    }

    void Start()
    {
        controller = GetComponent<CharacterController>();
        
        if (cameraTransform == null && Camera.main != null)
        {
            cameraTransform = Camera.main.transform;
        }
    }

    void Update()
    {
        if (rightClickAction.IsPressed())
        {
            Vector2 lookDelta = lookAction.ReadValue<Vector2>();
            
            transform.Rotate(0, lookDelta.x * lookSpeedX * Time.deltaTime, 0);

            cameraPitch -= lookDelta.y * lookSpeedY * Time.deltaTime;
            cameraPitch = Mathf.Clamp(cameraPitch, -80f, 80f);
        }

        Vector2 moveInput = moveAction.ReadValue<Vector2>();
        Vector3 move = transform.forward * moveInput.y + transform.right * moveInput.x;
        
        if (move.magnitude > 1f) move.Normalize();
        move *= moveSpeed;

        if (controller.isGrounded) 
        {
            verticalVelocity = -0.5f; 

            if (jumpAction.WasPressedThisFrame()) 
            {
                verticalVelocity = jumpForce;
            }
        } 
        else 
        {
            verticalVelocity -= 9.81f * Time.deltaTime;
        }
        
        move.y = verticalVelocity;
        controller.Move(move * Time.deltaTime);
    }

    void LateUpdate()
    {
        if (cameraTransform != null)
        {
            Quaternion camRotation = transform.rotation * Quaternion.Euler(cameraPitch, 0f, 0f);

            Vector3 pivot = transform.position + Vector3.up * cameraOffset.y;

            Vector3 targetPos = pivot + (camRotation * new Vector3(0, 0, cameraOffset.z));
            
            cameraTransform.position = Vector3.Lerp(cameraTransform.position, targetPos, cameraFollowSpeed * Time.deltaTime);
            cameraTransform.rotation = Quaternion.Lerp(cameraTransform.rotation, camRotation, cameraFollowSpeed * Time.deltaTime);
        }
    }
}