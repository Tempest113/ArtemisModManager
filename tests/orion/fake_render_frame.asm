; Stands in for the game's frame-pump helper when the render throttle is tested. Like the real one it starts with the 5-byte instruction
; "mov [rsp+8], rbx" (48 89 5C 24 08), uses rbx, and restores it. It records its four register arguments' sum and how many times its body ran.
PUBLIC FakeRenderFrame
PUBLIC g_ran
PUBLIC g_sum

.data
g_ran dq 0
g_sum dq 0

.code
FakeRenderFrame PROC
    mov [rsp+8], rbx
    lea rbx, [rcx+rdx]
    add rbx, r8
    add rbx, r9
    mov g_sum, rbx
    inc qword ptr g_ran
    mov rbx, [rsp+8]
    ret
FakeRenderFrame ENDP
END
