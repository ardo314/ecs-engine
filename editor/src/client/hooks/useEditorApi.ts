import { useCallback } from "react";
import { API_BASE } from "../config";

export function useEditorApi() {
  const createEntity = useCallback(async () => {
    await fetch(`${API_BASE}/api/entities`, { method: "POST" });
  }, []);

  const deleteEntity = useCallback(async (entityId: number) => {
    await fetch(`${API_BASE}/api/entities/${entityId}`, { method: "DELETE" });
  }, []);

  const removeComponent = useCallback(
    async (entityId: number, componentType: string) => {
      await fetch(
        `${API_BASE}/api/entities/${entityId}/components/${encodeURIComponent(componentType)}`,
        { method: "DELETE" },
      );
    },
    [],
  );

  return { createEntity, deleteEntity, removeComponent };
}
