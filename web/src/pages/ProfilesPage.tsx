import { Box } from '@mantine/core';
import { Navigate, Route, Routes } from 'react-router';
import { ProfileEditor } from '@/features/profiles/ProfileEditor';
import { ProfilesList } from '@/features/profiles/ProfilesList';

/** `/profiles` (list) and `/profiles/:id` (editor). */
export function ProfilesPage() {
  return (
    // Bottom padding keeps the last controls clear of the floating save bar.
    <Box maw={1200} pb={96}>
      <Routes>
        <Route index element={<Box maw={980}><ProfilesList /></Box>} />
        <Route path=":id" element={<ProfileEditor />} />
        <Route path="*" element={<Navigate to="/profiles" replace />} />
      </Routes>
    </Box>
  );
}
