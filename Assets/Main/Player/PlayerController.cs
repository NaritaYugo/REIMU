using UnityEngine;

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
    public float lookSpeedX = 150f;
    public float lookSpeedY = 100f;

    private CharacterController controller;
    private float verticalVelocity = 0f;
    private float cameraPitch = 0f;

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
        if (Input.GetMouseButton(1))
        {
            float mouseX = Input.GetAxis("Mouse X");
            transform.Rotate(0, mouseX * lookSpeedX * Time.deltaTime, 0);

            float mouseY = Input.GetAxis("Mouse Y");
            cameraPitch -= mouseY * lookSpeedY * Time.deltaTime;

            cameraPitch = Mathf.Clamp(cameraPitch, -80f, 80f);
        }

        float h = Input.GetAxis("Horizontal");
        float v = Input.GetAxis("Vertical");

        Vector3 move = transform.forward * v + transform.right * h;
        
        if (move.magnitude > 1f) move.Normalize();
        move *= moveSpeed;

        if (controller.isGrounded) {
            verticalVelocity = -0.5f; 

            if (Input.GetButtonDown("Jump")) {
                verticalVelocity = jumpForce;
            }
        } else {
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