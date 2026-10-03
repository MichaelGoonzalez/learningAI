"""Punto de entrada del backend.

La aplicacion FastAPI se incorporara en el bloque 6. Este modulo permanece
importable desde el inicio para conservar un punto de entrada estable.
"""

from app.core.config import get_settings


def main() -> None:
    """Valida la configuracion hasta que se conecte el ciclo de ejecucion."""
    settings = get_settings()
    print(
        "Configuration loaded: "
        f"{len(settings.cameras)} camera(s), model={settings.model.weights}"
    )


if __name__ == "__main__":
    main()

