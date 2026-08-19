# Registro de cambios

## rev01

### 1. Cerrar el servidor TCP al detener el servicio

Al elegir detener el servicio, ahora también se cierra el servidor TCP que escucha en el puerto 8881. Esto libera el puerto para poder iniciar una nueva prueba o conexión sin que el servidor anterior se quede abierto.

### 2. No reportar como error el cierre normal del servidor

Así nos evitamos reportar como error el cierre normal del servidor cuando el usuario detiene el servicio.

### 3. Evitar iniciar un segundo servidor cuando ya hay uno activo

Hay que restringir el que se intente iniciar un segundo servidor cuando ya haya uno activo, porque ps puede haber conflictos intrafamiliares.
