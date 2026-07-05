import { useCallback } from "react";

const API_URL = import.meta.env.VITE_API_URL ?? "http://localhost:5000";

export function useEditorApi() {
  const createEntity = useCallback(async () => {
    await fetch(`${API_URL}/api/entities`, { method: "POST" });
  }, []);

  const deleteEntity = useCallback(async (entityId: number) => {
    await fetch(`${API_URL}/api/entities/${entityId}`, { method: "DELETE" });
  }, []);

  const removeComponent = useCallback(
    async (entityId: number, componentType: string) => {
      await fetch(
        `${API_URL}/api/entities/${entityId}/components/${encodeURIComponent(componentType)}`,
        { method: "DELETE" },
      );
    },
    [],
  );

  return { createEntity, deleteEntity, removeComponent };
}
