import { useCallback } from "react";
import { BACKEND_URL } from "../config";

export function useEditorApi() {
  const createEntity = useCallback(async () => {
    await fetch(`${BACKEND_URL}/api/entities`, { method: "POST" });
  }, []);

  const deleteEntity = useCallback(async (entityId: number) => {
    await fetch(`${BACKEND_URL}/api/entities/${entityId}`, { method: "DELETE" });
  }, []);

  const removeComponent = useCallback(
    async (entityId: number, componentType: string) => {
      await fetch(
        `${BACKEND_URL}/api/entities/${entityId}/components/${encodeURIComponent(componentType)}`,
        { method: "DELETE" },
      );
    },
    [],
  );

  return { createEntity, deleteEntity, removeComponent };
}
